using System.Threading;

namespace Company.Observability.Logging;

/// <summary>
/// Thread-safe <see cref="AsyncLocal{T}"/> store for correlation IDs set by
/// <see cref="CorrelationMiddleware"/>.
/// </summary>
/// <remarks>
/// Values are stored in <see cref="AsyncLocal{T}"/> so that they flow naturally
/// through async continuations on the same logical call context.
/// Background tasks can set their own value without polluting other requests.
/// </remarks>
public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> _correlationId = new();
    private static readonly AsyncLocal<string?> _requestId = new();

    /// <summary>
    /// Gets or sets the correlation identifier for the current asynchronous execution context.
    /// Typically sourced from the <c>X-Correlation-Id</c> request header, or generated on first
    /// request if the header is absent.
    /// </summary>
    public static string? CorrelationId
    {
        get => _correlationId.Value;
        set => _correlationId.Value = value;
    }

    /// <summary>
    /// Gets or sets the request identifier for the current asynchronous execution context.
    /// Mirrors <see cref="CorrelationId"/> by default; can be overridden independently
    /// (e.g. to carry an internal request tracking ID alongside an external correlation ID).
    /// </summary>
    public static string? RequestId
    {
        get => _requestId.Value;
        set => _requestId.Value = value;
    }

    /// <summary>
    /// Clears both stored identifiers. Called automatically at the end of each request
    /// by <see cref="CorrelationMiddleware"/> to prevent stale values leaking into
    /// thread-pool threads that are reused for subsequent requests.
    /// </summary>
    internal static void Clear()
    {
        _correlationId.Value = null;
        _requestId.Value = null;
    }
}
