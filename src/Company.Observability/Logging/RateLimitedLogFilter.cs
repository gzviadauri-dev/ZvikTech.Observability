using System;
using System.Collections.Concurrent;
using System.Threading;
using Company.Observability.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace Company.Observability.Logging;

/// <summary>
/// Serilog <see cref="ILogEventFilter"/> that suppresses repetitive Warning (and below) messages
/// from flooding log aggregators.
/// </summary>
/// <remarks>
/// Behaviour:
/// <list type="bullet">
///   <item>Uses the raw <see cref="MessageTemplate.Text"/> as the deduplication key so that
///         different parameter values of the same template share the same bucket.</item>
///   <item>Allows at most <see cref="RateLimitOptions.MaxPerWindow"/> occurrences of a given
///         template within a rolling <see cref="RateLimitOptions.WindowSeconds"/>-second window.</item>
///   <item>Error and Fatal events always pass through unconditionally.</item>
///   <item>Expired window state is cleaned up on every 1 000th call to <see cref="IsEnabled"/>
///         to bound memory growth without requiring a background timer.</item>
/// </list>
/// Thread-safe: all state mutations are protected with per-bucket locking.
/// </remarks>
public sealed class RateLimitedLogFilter : ILogEventFilter
{
    private readonly RateLimitOptions _options;
    private readonly ConcurrentDictionary<string, RateLimitBucket> _buckets = new();
    private long _totalCallCount;

    /// <summary>
    /// Initialises the filter with the resolved rate-limit configuration.
    /// </summary>
    /// <param name="options">Rate-limit configuration.</param>
    public RateLimitedLogFilter(RateLimitOptions options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public bool IsEnabled(LogEvent logEvent)
    {
        // Error / Fatal are never rate-limited.
        if (logEvent.Level >= LogEventLevel.Error)
        {
            return true;
        }

        // Rate limiting disabled by configuration.
        if (!_options.Enabled)
        {
            return true;
        }

        var key = logEvent.MessageTemplate.Text;
        var now = DateTime.UtcNow;

        var bucket = _buckets.GetOrAdd(key, _ => new RateLimitBucket(now));

        bool allowed;
        lock (bucket)
        {
            // Roll the window forward if it has expired.
            if ((now - bucket.WindowStart).TotalSeconds >= _options.WindowSeconds)
            {
                bucket.Count = 0;
                bucket.WindowStart = now;
            }

            bucket.Count++;
            allowed = bucket.Count <= _options.MaxPerWindow;
        }

        // Periodic cleanup: remove stale buckets every 1 000th call to avoid unbounded growth.
        long callCount = Interlocked.Increment(ref _totalCallCount);
        if (callCount % 1_000 == 0)
        {
            CleanupStaleBuckets(now);
        }

        return allowed;
    }

    private void CleanupStaleBuckets(DateTime now)
    {
        foreach (var kvp in _buckets)
        {
            bool stale;
            lock (kvp.Value)
            {
                stale = (now - kvp.Value.WindowStart).TotalSeconds >= _options.WindowSeconds * 2;
            }

            if (stale)
            {
                _buckets.TryRemove(kvp.Key, out _);
            }
        }
    }

    private sealed class RateLimitBucket
    {
        internal int Count;
        internal DateTime WindowStart;

        internal RateLimitBucket(DateTime windowStart)
        {
            WindowStart = windowStart;
            Count = 0;
        }
    }
}
