using System;
using System.Diagnostics;
using Company.Observability.Configuration;
using Microsoft.Extensions.Options;

namespace Company.Observability.Telemetry;

/// <summary>
/// Factory that creates and caches a named <see cref="ActivitySource"/> for application code to
/// produce custom OpenTelemetry spans without hard-coding source names.
/// </summary>
public interface IActivitySourceFactory
{
    /// <summary>
    /// Returns the singleton <see cref="ActivitySource"/> for this service.
    /// The source name is <c>"Company.{ServiceName}"</c>.
    /// </summary>
    /// <param name="name">
    /// Reserved for future use; currently ignored. Pass <c>null</c> to use the default source.
    /// </param>
    /// <returns>The cached <see cref="ActivitySource"/> instance.</returns>
    ActivitySource GetSource(string? name = null);
}

/// <summary>
/// Default implementation of <see cref="IActivitySourceFactory"/>.
/// Registered as a singleton; the underlying <see cref="ActivitySource"/> is disposed when
/// the DI container is torn down.
/// </summary>
internal sealed class ActivitySourceFactory : IActivitySourceFactory, IDisposable
{
    private readonly ActivitySource _source;
    private bool _disposed;

    /// <summary>
    /// Initialises the factory and creates the application-level <see cref="ActivitySource"/>.
    /// </summary>
    /// <param name="options">Resolved observability options.</param>
    public ActivitySourceFactory(IOptions<ObservabilityOptions> options)
    {
        var o = options.Value;
        var version = o.ServiceVersion ?? "1.0.0";
        _source = new ActivitySource($"Company.{o.ServiceName}", version);
    }

    /// <inheritdoc />
    public ActivitySource GetSource(string? name = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _source;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _source.Dispose();
            _disposed = true;
        }
    }
}
