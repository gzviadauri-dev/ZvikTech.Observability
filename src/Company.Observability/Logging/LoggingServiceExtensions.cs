using System;
using System.Reflection;
using Company.Observability.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.Graylog;
using Serilog.Sinks.Graylog.Core.Transport;

namespace Company.Observability.Logging;

/// <summary>
/// Extension methods for configuring the Serilog-based logging pipeline.
/// </summary>
public static class LoggingServiceExtensions
{
    /// <summary>
    /// Configures two-stage Serilog initialisation on the <see cref="WebApplicationBuilder"/>.
    /// </summary>
    /// <remarks>
    /// Stage 1 — a lightweight bootstrap logger is created immediately so that any exceptions
    /// thrown during host construction are captured and emitted.
    /// Stage 2 — after the DI container is fully built, the logger is re-configured from
    /// <see cref="IOptions{TOptions}"/> so that all enrichers, sinks, and filters driven by
    /// <see cref="ObservabilityOptions"/> are active.
    /// </remarks>
    /// <param name="builder">The web application builder.</param>
    /// <param name="configure">Optional inline delegate to override options before binding.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    public static WebApplicationBuilder UseCompanyLogging(
        this WebApplicationBuilder builder,
        Action<ObservabilityOptions>? configure = null)
    {
        // ── Stage 1: Bootstrap logger ──────────────────────────────────────────────────────
        // Must be set BEFORE builder.Build() so that DI-setup exceptions are captured.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(new CompactJsonFormatter())
            .CreateBootstrapLogger();

        // ── Stage 2: Full DI-aware configuration ──────────────────────────────────────────
        // The callback runs after the host is built, so IOptions<ObservabilityOptions> and
        // all other DI services are available.
        builder.Host.UseSerilog((ctx, services, cfg) =>
        {
            var options = services.GetRequiredService<IOptions<ObservabilityOptions>>().Value;
            ConfigureFullLogger(cfg, options, ctx.Configuration);
        });

        // Register supporting DI services.
        builder.Services.AddCompanyLogging(builder.Configuration, builder.Environment);

        return builder;
    }

    /// <summary>
    /// Registers Serilog-related DI services (middleware, enrichers) without modifying
    /// the logger configuration.  Called internally by <see cref="UseCompanyLogging"/>;
    /// can also be called directly in non-WebApplication hosts.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="env">Hosting environment.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddCompanyLogging(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment env)
    {
        services.AddTransient<CorrelationMiddleware>();
        return services;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────────────────

    internal static void ConfigureFullLogger(
        LoggerConfiguration cfg,
        ObservabilityOptions options,
        IConfiguration configuration)
    {
        var minimumLevel = ParseLogLevel(options.Logging.MinimumLevel);

        cfg
            // Minimum level and overrides
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("System", LogEventLevel.Warning)

            // Allow appsettings Serilog section to override levels per-namespace.
            .ReadFrom.Configuration(configuration)

            // ── Enrichers ──────────────────────────────────────────────────────────────
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service.name", options.ServiceName)
            .Enrich.WithProperty("service.version",
                options.ServiceVersion ?? GetEntryAssemblyVersion())
            .Enrich.WithProperty("deployment.environment",
                options.Environment ?? System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "unknown")
            .Enrich.WithMachineName()
            .Enrich.WithProcessId()
            .Enrich.WithThreadId()
            .Enrich.With<TraceEnricher>()
            .Enrich.With(new CorrelationContextEnricher())
            .Enrich.With(new RedactionEnricher(options))

            // ── Filters ────────────────────────────────────────────────────────────────
            .Filter.With(new SamplingFilter(options))
            .Filter.With(new RateLimitedLogFilter(options.Logging.RateLimit))

            // ── Sinks (all wrapped in async buffer) ────────────────────────────────────
            .WriteTo.Async(a =>
            {
                if (options.Logging.EnableConsoleOutput)
                {
                    a.Console(new CompactJsonFormatter());
                }

                if (options.Logging.Graylog.Enabled)
                {
                    ConfigureGraylogSink(a, options.Logging.Graylog);
                }
            }, bufferSize: 10_000, blockWhenFull: false);
    }

    private static void ConfigureGraylogSink(Serilog.Configuration.LoggerSinkConfiguration a, GraylogOptions graylogOptions)
    {
        try
        {
            var transportType = graylogOptions.Protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                ? TransportType.Tcp
                : TransportType.Udp;

            var sinkOptions = new GraylogSinkOptions
            {
                HostnameOrAddress = graylogOptions.Host!,
                Port = graylogOptions.Port,
                TransportType = transportType,
            };

            if (!string.IsNullOrWhiteSpace(graylogOptions.Facility))
            {
                sinkOptions.Facility = graylogOptions.Facility;
            }

            a.Graylog(sinkOptions);
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine($"[Company.Observability] Failed to configure Graylog sink: {ex.Message}");
        }
    }

    private static LogEventLevel ParseLogLevel(string level)
    {
        return Enum.TryParse<LogEventLevel>(level, ignoreCase: true, out var parsed)
            ? parsed
            : LogEventLevel.Information;
    }

    private static string GetEntryAssemblyVersion()
    {
        return Assembly.GetEntryAssembly()
                   ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
               ?? "1.0.0";
    }

    /// <summary>
    /// Configures the <c>UseSerilogRequestLogging</c> middleware with ECS-compatible
    /// diagnostic context enrichment and level overrides.
    /// </summary>
    internal static void ConfigureRequestLogging(
        Serilog.AspNetCore.RequestLoggingOptions opts,
        ObservabilityOptions options)
    {
        opts.GetLevel = (httpContext, _, ex) =>
        {
            // Excluded health/metrics paths → Verbose (effectively suppressed).
            if (options.Logging.ExcludePaths is { Length: > 0 })
            {
                foreach (var path in options.Logging.ExcludePaths)
                {
                    if (httpContext.Request.Path.StartsWithSegments(path))
                    {
                        return LogEventLevel.Verbose;
                    }
                }
            }

            if (ex != null || httpContext.Response.StatusCode >= 500)
            {
                return LogEventLevel.Error;
            }

            if (httpContext.Response.StatusCode >= 400)
            {
                return LogEventLevel.Warning;
            }

            return LogEventLevel.Information;
        };

        opts.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("http.method", httpContext.Request.Method);

            // Prefer the route template (e.g. "/orders/{id}") over the literal path.
            var endpoint = httpContext.GetEndpoint();
            var route = (endpoint as RouteEndpoint)?.RoutePattern?.RawText
                        ?? httpContext.Request.Path.Value
                        ?? "unknown";

            diagnosticContext.Set("http.route", route);
            diagnosticContext.Set("http.status_code", httpContext.Response.StatusCode);
            diagnosticContext.Set("http.scheme", httpContext.Request.Scheme);
            diagnosticContext.Set("client.ip",
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            diagnosticContext.Set("user_agent.original",
                httpContext.Request.Headers.UserAgent.ToString());
        };
    }

    /// <summary>
    /// Inline enricher that reads from <see cref="CorrelationContext"/> at log-event time
    /// so that correlation IDs set by background workers (outside HTTP middleware) also appear
    /// in their log events.
    /// </summary>
    private sealed class CorrelationContextEnricher : Serilog.Core.ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, Serilog.Core.ILogEventPropertyFactory propertyFactory)
        {
            var correlationId = CorrelationContext.CorrelationId;
            if (!string.IsNullOrEmpty(correlationId))
            {
                logEvent.AddOrUpdateProperty(
                    propertyFactory.CreateProperty("correlation_id", correlationId));
            }

            var requestId = CorrelationContext.RequestId;
            if (!string.IsNullOrEmpty(requestId))
            {
                logEvent.AddOrUpdateProperty(
                    propertyFactory.CreateProperty("request_id", requestId));
            }
        }
    }
}
