using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Company.Observability.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Serilog.Context;

namespace Company.Observability.Logging;

/// <summary>
/// ASP.NET Core middleware that establishes a correlation identifier for every inbound HTTP
/// request and makes it available across the full request pipeline.
/// </summary>
/// <remarks>
/// Behaviour:
/// <list type="number">
///   <item>Reads the configured correlation header (default <c>X-Correlation-Id</c>) from the request.</item>
///   <item>If the header is absent or blank, generates a new <see cref="Guid"/> in round-trip (<c>"D"</c>) format.</item>
///   <item>Stores the value in <see cref="CorrelationContext"/> for access anywhere in the call chain.</item>
///   <item>Pushes the value into the Serilog <c>LogContext</c> so it appears on every log event.</item>
///   <item>Appends the correlation ID to the HTTP response via <c>Response.OnStarting</c>.</item>
///   <item>Starts an <see cref="Activity"/> for the request ONLY when <see cref="Activity.Current"/> is
///        <c>null</c>, preventing a double-span when the OpenTelemetry ASP.NET Core instrumentation is active.</item>
/// </list>
/// Register as a <c>transient</c> service; the DI container resolves it per request.
/// </remarks>
public sealed class CorrelationMiddleware : IMiddleware
{
    private readonly ObservabilityOptions _options;

    /// <summary>
    /// Initialises the middleware with resolved observability options.
    /// </summary>
    /// <param name="options">Resolved observability options.</param>
    public CorrelationMiddleware(IOptions<ObservabilityOptions> options)
    {
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var headerName = _options.Logging.CorrelationHeaderName;

        // Read or generate the correlation ID.
        var correlationId = context.Request.Headers[headerName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString("D");
        }

        CorrelationContext.CorrelationId = correlationId;
        CorrelationContext.RequestId = correlationId;

        // Schedule response header before the response starts (headers become read-only after that).
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[headerName] = correlationId;
            return Task.CompletedTask;
        });

        // Start a manual Activity only when OTEL instrumentation has not already started one.
        // This allows tools that inspect Activity.Current (e.g. TraceEnricher) to work even
        // in environments without the OpenTelemetry middleware, while avoiding a double-span
        // when the OTEL ASP.NET Core instrumentation IS active.
        Activity? manualActivity = null;
        if (Activity.Current is null)
        {
            var activityName = $"HTTP {context.Request.Method} {context.Request.Path}";
            manualActivity = new ActivitySource("Company.Observability.Correlation")
                .StartActivity(activityName, ActivityKind.Server);
        }

        // Push correlation_id into the Serilog LogContext so every log event on this
        // request automatically carries the property.
        using (LogContext.PushProperty("correlation_id", correlationId))
        using (LogContext.PushProperty("request_id", correlationId))
        {
            try
            {
                await next(context);
            }
            finally
            {
                manualActivity?.Stop();
                manualActivity?.Dispose();
                CorrelationContext.Clear();
            }
        }
    }
}
