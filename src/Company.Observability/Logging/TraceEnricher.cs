using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace Company.Observability.Logging;

/// <summary>
/// Serilog enricher that adds W3C-format trace context from <see cref="Activity.Current"/>
/// to every log event.
/// </summary>
/// <remarks>
/// Properties added:
/// <list type="bullet">
///   <item><term>trace_id</term><description>32 lower-case hex characters (128-bit trace ID)</description></item>
///   <item><term>span_id</term><description>16 lower-case hex characters (64-bit span ID)</description></item>
/// </list>
/// If no <see cref="Activity"/> is currently active the enricher is a no-op; it never throws.
/// Property name strings are cached to avoid per-event allocations.
/// </remarks>
public sealed class TraceEnricher : ILogEventEnricher
{
    // Cached property-name constants to avoid string allocations on every log event.
    private const string TraceIdPropertyName = "trace_id";
    private const string SpanIdPropertyName = "span_id";

    private static readonly LogEventProperty EmptyTrace =
        new(TraceIdPropertyName, new ScalarValue(string.Empty));

    private static readonly LogEventProperty EmptySpan =
        new(SpanIdPropertyName, new ScalarValue(string.Empty));

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var activity = Activity.Current;

        if (activity is null)
        {
            // Do not pollute the event with empty stubs — simply skip.
            return;
        }

        // ActivityTraceId.ToString() returns a 32 lower-case hex chars (W3C format).
        var traceId = activity.TraceId.ToString();
        // ActivitySpanId.ToString() returns 16 lower-case hex chars.
        var spanId = activity.SpanId.ToString();

        logEvent.AddOrUpdateProperty(
            propertyFactory.CreateProperty(TraceIdPropertyName, traceId));

        logEvent.AddOrUpdateProperty(
            propertyFactory.CreateProperty(SpanIdPropertyName, spanId));
    }
}
