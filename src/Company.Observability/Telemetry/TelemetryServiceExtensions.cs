using System;
using System.Collections.Generic;
using System.Reflection;
using Company.Observability.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog.Debugging;

namespace Company.Observability.Telemetry;

/// <summary>
/// Extension methods for registering the OpenTelemetry tracing and metrics pipelines.
/// </summary>
public static class TelemetryServiceExtensions
{
    /// <summary>
    /// Registers OpenTelemetry tracing and metrics pipelines using values bound from the
    /// <c>"Observability"</c> configuration section.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Configuration is read directly from <paramref name="configuration"/> at startup so that
    /// all SDK builder callbacks (filters, samplers, exporters) have the correct values.
    /// The <c>IOptions&lt;T&gt;</c> pattern is used at request time for per-request
    /// filter decisions via <c>HttpContext.RequestServices</c>.
    /// </para>
    /// <para>
    /// If <see cref="MetricsOptions.PrometheusEnabled"/> is <c>true</c>, call
    /// <c>app.UseCompanyObservability()</c> (or <c>app.MapPrometheusScrapingEndpoint("/metrics")</c>
    /// directly) after <c>builder.Build()</c> to expose the scraping endpoint.
    /// </para>
    /// <para>
    /// SqlClient instrumentation is opt-in.  Set <c>Observability:Telemetry:Tracing:SqlClientEnabled=true</c>
    /// <strong>and</strong> uncomment the <c>OpenTelemetry.Instrumentation.SqlClient</c>
    /// PackageReference in the library <c>.csproj</c>, then define the compile constant
    /// <c>SQL_CLIENT_INSTRUMENTATION</c> to activate it.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration used to bind options at startup.</param>
    /// <param name="env">Hosting environment used to populate the <c>deployment.environment</c> resource attribute.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddCompanyTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment env)
    {
        // Snapshot options from configuration at startup.
        // This avoids circular DI dependencies inside the SDK builder callbacks.
        var options = SnapshotOptions(configuration, env);

        if (!options.Telemetry.Enabled)
        {
            return services;
        }

        var resourceBuilder = BuildResourceBuilder(options, env);

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(rb =>
            {
                rb.AddService(
                    serviceName: options.ServiceName,
                    serviceVersion: options.ServiceVersion ?? GetEntryAssemblyVersion());
                rb.AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = env.EnvironmentName,
                    ["host.name"] = System.Environment.MachineName,
                    ["process.id"] = System.Environment.ProcessId
                });
            });

        if (options.Telemetry.Tracing.Enabled)
        {
            otel.WithTracing(tracing => ConfigureTracing(tracing, options, resourceBuilder));
        }

        if (options.Telemetry.Metrics.Enabled)
        {
            otel.WithMetrics(metrics => ConfigureMetrics(metrics, options, resourceBuilder));
        }

        return services;
    }

    // ── Resource ─────────────────────────────────────────────────────────────────────────

    private static ResourceBuilder BuildResourceBuilder(ObservabilityOptions options, IHostEnvironment env)
    {
        return ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: options.ServiceName,
                serviceVersion: options.ServiceVersion ?? GetEntryAssemblyVersion())
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] = env.EnvironmentName,
                ["host.name"] = System.Environment.MachineName,
                ["process.id"] = System.Environment.ProcessId
            });
    }

    // ── Tracing ──────────────────────────────────────────────────────────────────────────

    private static void ConfigureTracing(
        TracerProviderBuilder tracing,
        ObservabilityOptions options,
        ResourceBuilder resourceBuilder)
    {
        var tracingOpts = options.Telemetry.Tracing;

        tracing.SetResourceBuilder(resourceBuilder);

        // Listen to the application's custom ActivitySource.
        tracing.AddSource($"Company.{options.ServiceName}");
        tracing.AddSource("Company.Observability.*");

        // Head-based sampling.
        if (tracingOpts.SamplingRatio >= 1.0)
        {
            tracing.SetSampler(new AlwaysOnSampler());
        }
        else
        {
            tracing.SetSampler(
                new ParentBasedSampler(new TraceIdRatioBasedSampler(tracingOpts.SamplingRatio)));
        }

        // ASP.NET Core instrumentation with path-based filter.
        // The filter closure captures the excludePaths array from startup options.
        var excludePaths = tracingOpts.ExcludePaths;
        tracing.AddAspNetCoreInstrumentation(opts =>
        {
            opts.RecordException = true;
            opts.Filter = httpContext =>
            {
                if (excludePaths is { Length: > 0 })
                {
                    foreach (var path in excludePaths)
                    {
                        if (httpContext.Request.Path.StartsWithSegments(path))
                        {
                            return false;
                        }
                    }
                }

                return true;
            };

            opts.EnrichWithHttpRequest = (activity, httpRequest) =>
            {
                if (httpRequest.ContentLength.HasValue)
                {
                    activity.SetTag("http.request.content_length", httpRequest.ContentLength.Value);
                }

                activity.SetTag("user_agent.original", httpRequest.Headers.UserAgent.ToString());
            };

            opts.EnrichWithHttpResponse = (activity, httpResponse) =>
            {
                if (httpResponse.ContentLength.HasValue)
                {
                    activity.SetTag("http.response.content_length", httpResponse.ContentLength.Value);
                }
            };
        });

        // HttpClient instrumentation — filter out calls to health/metrics endpoints.
        tracing.AddHttpClientInstrumentation(opts =>
        {
            opts.RecordException = true;
            opts.FilterHttpRequestMessage = request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                return !path.Equals("/health", StringComparison.OrdinalIgnoreCase) &&
                       !path.Equals("/metrics", StringComparison.OrdinalIgnoreCase);
            };
        });

        // SqlClient instrumentation (opt-in, requires separate NuGet package).
#if SQL_CLIENT_INSTRUMENTATION
        if (tracingOpts.SqlClientEnabled)
        {
            tracing.AddSqlClientInstrumentation(opts =>
            {
                opts.RecordException = true;
                opts.SetDbStatementForText = true;
            });
        }
#else
        if (tracingOpts.SqlClientEnabled)
        {
            SelfLog.WriteLine(
                "[Company.Observability] Tracing:SqlClientEnabled = true but the " +
                "SQL_CLIENT_INSTRUMENTATION compile constant is not defined. " +
                "Uncomment the OpenTelemetry.Instrumentation.SqlClient PackageReference " +
                "in Company.Observability.csproj and add the compile constant to enable it.");
        }
#endif

        // OTLP exporter.
        if (options.Telemetry.Otlp.Enabled)
        {
            try
            {
                tracing.AddOtlpExporter(o => ApplyOtlpOptions(o, options.Telemetry.Otlp));
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine(
                    $"[Company.Observability] Failed to configure OTLP trace exporter: {ex.Message}");
            }
        }
    }

    // ── Metrics ──────────────────────────────────────────────────────────────────────────

    private static void ConfigureMetrics(
        MeterProviderBuilder metrics,
        ObservabilityOptions options,
        ResourceBuilder resourceBuilder)
    {
        metrics.SetResourceBuilder(resourceBuilder);

        // Listen to the application's custom Meter.
        metrics.AddMeter($"Company.{options.ServiceName}");
        metrics.AddMeter("Company.Observability.*");

        metrics.AddAspNetCoreInstrumentation();
        metrics.AddHttpClientInstrumentation();
        metrics.AddRuntimeInstrumentation();
        // AddProcessInstrumentation() — the package (OpenTelemetry.Instrumentation.Process)
        // has no version compatible with OpenTelemetry >= 1.9.0 yet.
        // Process metrics (CPU, memory, thread count) are partially covered by
        // AddRuntimeInstrumentation(). Re-enable when a compatible release is published.

        // Prometheus scraping endpoint (RC package — see README for stability notes).
        if (options.Telemetry.Metrics.PrometheusEnabled)
        {
            metrics.AddPrometheusExporter();
        }

        // OTLP exporter.
        if (options.Telemetry.Otlp.Enabled)
        {
            try
            {
                metrics.AddOtlpExporter(o => ApplyOtlpOptions(o, options.Telemetry.Otlp));
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine(
                    $"[Company.Observability] Failed to configure OTLP metrics exporter: {ex.Message}");
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    internal static void ApplyOtlpOptions(OtlpExporterOptions o, OtlpOptions otlpConfig)
    {
        o.Endpoint = new Uri(otlpConfig.Endpoint);
        o.Protocol = otlpConfig.Protocol.Equals("http/protobuf", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;

        if (otlpConfig.Headers is { Count: > 0 })
        {
            // OTLP headers are passed as "key1=value1,key2=value2".
            // Values are URL-encoded to handle special characters safely.
            // NOTE: header values (e.g. API keys) are never written to application logs.
            var sb = new System.Text.StringBuilder();
            foreach (var kvp in otlpConfig.Headers)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(Uri.EscapeDataString(kvp.Key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(kvp.Value));
            }

            o.Headers = sb.ToString();
        }
    }

    private static ObservabilityOptions SnapshotOptions(IConfiguration configuration, IHostEnvironment env)
    {
        var options = new ObservabilityOptions();
        configuration.GetSection(ObservabilityOptions.SectionName).Bind(options);

        // Apply environment default if not set.
        options.Environment ??= env.EnvironmentName;

        return options;
    }

    private static string GetEntryAssemblyVersion()
    {
        return Assembly.GetEntryAssembly()
                   ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
               ?? "1.0.0";
    }
}
