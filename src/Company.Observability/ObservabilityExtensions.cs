using System;
using Company.Observability.Configuration;
using Company.Observability.Logging;
using Company.Observability.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

namespace Company.Observability;

/// <summary>
/// Primary entry-point extension methods for wiring Company.Observability into an
/// ASP.NET Core application.
/// </summary>
/// <example>
/// <code>
/// // Program.cs — before builder.Build()
/// builder.AddCompanyObservability();
///
/// var app = builder.Build();
///
/// // Program.cs — after builder.Build()
/// app.UseCompanyObservability();
/// </code>
/// </example>
public static class ObservabilityExtensions
{
    /// <summary>
    /// Registers all observability services (logging, tracing, metrics, correlation) and
    /// validates configuration at startup.
    /// </summary>
    /// <remarks>
    /// Call this method on the <see cref="WebApplicationBuilder"/> <strong>before</strong>
    /// calling <c>builder.Build()</c>.  The method:
    /// <list type="number">
    ///   <item>Binds and validates <see cref="ObservabilityOptions"/> from the
    ///       <c>"Observability"</c> configuration section.</item>
    ///   <item>Sets up two-stage Serilog initialisation (bootstrap logger + full
    ///       DI-aware reconfiguration).</item>
    ///   <item>Registers the OpenTelemetry tracing and metrics pipelines.</item>
    ///   <item>Registers <see cref="CorrelationMiddleware"/> as <c>transient</c>.</item>
    ///   <item>Registers <see cref="IActivitySourceFactory"/> as <c>singleton</c>.</item>
    ///   <item>Registers <see cref="IMeterAccessor"/> as <c>singleton</c>.</item>
    ///   <item>Registers <see cref="Microsoft.AspNetCore.Http.IHttpContextAccessor"/>.</item>
    /// </list>
    /// </remarks>
    /// <param name="builder">The web application builder.</param>
    /// <param name="configure">
    /// Optional inline delegate to override individual option values before the
    /// configuration section is bound (useful in integration tests or for code-level defaults).
    /// </param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    public static WebApplicationBuilder AddCompanyObservability(
        this WebApplicationBuilder builder,
        Action<ObservabilityOptions>? configure = null)
    {
        // ── 1. Bind + validate ObservabilityOptions ────────────────────────────────────
        var optionsBuilder = builder.Services
            .AddOptions<ObservabilityOptions>()
            .BindConfiguration(ObservabilityOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        builder.Services.AddSingleton<IValidateOptions<ObservabilityOptions>,
            ObservabilityOptionsValidator>();

        // ── 2. Serilog (two-stage init — bootstrap + DI-aware reconfiguration) ─────────
        builder.UseCompanyLogging(configure);

        // ── 3. OpenTelemetry tracing + metrics ────────────────────────────────────────
        builder.Services.AddCompanyTelemetry(builder.Configuration, builder.Environment);

        // ── 4. CorrelationMiddleware (registered by AddCompanyLogging, but explicit here
        //       for clarity — AddTransient is idempotent) ─────────────────────────────
        builder.Services.AddTransient<CorrelationMiddleware>();

        // ── 5. IActivitySourceFactory singleton ───────────────────────────────────────
        builder.Services.AddSingleton<IActivitySourceFactory, ActivitySourceFactory>();

        // ── 6. IMeterAccessor singleton ───────────────────────────────────────────────
        builder.Services.AddSingleton<IMeterAccessor, MeterAccessor>();

        // ── 7. HttpContextAccessor (needed by some enrichers + endpoint helpers) ───────
        builder.Services.AddHttpContextAccessor();

        return builder;
    }

    /// <summary>
    /// Adds the observability middleware to the HTTP request pipeline.
    /// </summary>
    /// <remarks>
    /// Call this method on the <see cref="WebApplication"/> <strong>after</strong>
    /// <c>builder.Build()</c>, before any other <c>Use*</c> middleware registrations.
    /// The method, in order:
    /// <list type="number">
    ///   <item>Adds <see cref="CorrelationMiddleware"/> as the first middleware
    ///       so that every subsequent log event carries the correlation ID.</item>
    ///   <item>Adds <c>UseSerilogRequestLogging</c> for structured per-request log entries
    ///       with ECS-compatible fields and configurable level overrides.</item>
    ///   <item>If <see cref="MetricsOptions.PrometheusEnabled"/> is <c>true</c>, maps the
    ///       Prometheus scraping endpoint at <c>/metrics</c>.</item>
    ///   <item>Registers a graceful <see cref="Log.CloseAndFlush"/> on
    ///       <see cref="IHostApplicationLifetime.ApplicationStopped"/>.</item>
    /// </list>
    /// </remarks>
    /// <param name="app">The web application.</param>
    /// <returns>The same <paramref name="app"/> for chaining.</returns>
    public static WebApplication UseCompanyObservability(this WebApplication app)
    {
        // ── 1. Correlation middleware MUST be first ───────────────────────────────────
        app.UseMiddleware<CorrelationMiddleware>();

        // ── 2. Structured request logging ────────────────────────────────────────────
        var options = app.Services.GetRequiredService<IOptions<ObservabilityOptions>>().Value;

        app.UseSerilogRequestLogging(opts =>
            LoggingServiceExtensions.ConfigureRequestLogging(opts, options));

        // ── 3. Prometheus scraping endpoint (if enabled) ──────────────────────────────
        if (options.Telemetry.Metrics.PrometheusEnabled)
        {
            app.MapPrometheusScrapingEndpoint("/metrics");
        }

        // ── 4. Graceful flush on shutdown ─────────────────────────────────────────────
        // Ensures buffered async sink events are flushed before the process exits.
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopped.Register(Log.CloseAndFlush);

        return app;
    }
}
