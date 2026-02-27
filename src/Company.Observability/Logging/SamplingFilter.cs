using System.Threading;
using Company.Observability.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace Company.Observability.Logging;

/// <summary>
/// Serilog <see cref="ILogEventFilter"/> that probabilistically down-samples successful
/// HTTP request log events to reduce log-aggregator ingestion costs in high-traffic services.
/// </summary>
/// <remarks>
/// Rules:
/// <list type="bullet">
///   <item>Warning, Error, and Fatal events always pass through unconditionally.</item>
///   <item>Non-HTTP events (i.e. events without a <c>RequestPath</c> or <c>http.route</c> property) always pass through.</item>
///   <item>HTTP Information events are sampled at the configured <see cref="LoggingOptions.SampleSuccessRate"/>.</item>
///   <item>A counter incremented atomically with <c>Interlocked.Increment</c> ensures thread-safety without locking.</item>
/// </list>
/// Construct once and reuse — the internal counter is long-lived.
/// </remarks>
public sealed class SamplingFilter : ILogEventFilter
{
    private readonly double _sampleRate;
    private readonly int _modulo;
    private long _counter;

    /// <summary>
    /// Initialises the filter from resolved <see cref="ObservabilityOptions"/>.
    /// </summary>
    /// <param name="options">Resolved observability options.</param>
    public SamplingFilter(ObservabilityOptions options)
    {
        _sampleRate = options.Logging.SampleSuccessRate;

        // Compute the modulo divisor once. Avoid division by zero for rate == 0.
        _modulo = _sampleRate > 0.0 && _sampleRate < 1.0
            ? (int)System.Math.Ceiling(1.0 / _sampleRate)
            : 1;
    }

    /// <inheritdoc />
    public bool IsEnabled(LogEvent logEvent)
    {
        // Never suppress elevated-severity events.
        if (logEvent.Level >= LogEventLevel.Warning)
        {
            return true;
        }

        // If sample rate is 1.0 (keep everything) skip the check entirely.
        if (_sampleRate >= 1.0)
        {
            return true;
        }

        // Only apply sampling to HTTP request events identified by well-known properties.
        bool isHttpEvent =
            logEvent.Properties.ContainsKey("RequestPath") ||
            logEvent.Properties.ContainsKey("http.route");

        if (!isHttpEvent)
        {
            return true;
        }

        // Interlocked guarantees correct behaviour under concurrent request load.
        long count = Interlocked.Increment(ref _counter);
        return count % _modulo == 0;
    }
}
