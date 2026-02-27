using System;
using System.Diagnostics.Metrics;
using Company.Observability.Configuration;
using Microsoft.Extensions.Options;

namespace Company.Observability.Telemetry;

/// <summary>
/// Provides access to a named <see cref="Meter"/> for application code to create custom
/// OpenTelemetry metrics without hard-coding meter names.
/// </summary>
public interface IMeterAccessor
{
    /// <summary>
    /// Returns the singleton <see cref="Meter"/> for this service.
    /// The meter name is <c>"Company.{ServiceName}"</c>.
    /// </summary>
    /// <param name="name">
    /// Reserved for future use; currently ignored. Pass <c>null</c> to use the default meter.
    /// </param>
    /// <returns>The cached <see cref="Meter"/> instance.</returns>
    Meter GetMeter(string? name = null);
}

/// <summary>
/// Default implementation of <see cref="IMeterAccessor"/>.
/// Registered as a singleton; the underlying <see cref="Meter"/> is disposed when
/// the DI container is torn down.
/// </summary>
internal sealed class MeterAccessor : IMeterAccessor, IDisposable
{
    private readonly Meter _meter;
    private bool _disposed;

    /// <summary>
    /// Initialises the accessor and creates the application-level <see cref="Meter"/>.
    /// </summary>
    /// <param name="options">Resolved observability options.</param>
    public MeterAccessor(IOptions<ObservabilityOptions> options)
    {
        var o = options.Value;
        var version = o.ServiceVersion ?? "1.0.0";
        _meter = new Meter($"Company.{o.ServiceName}", version);
    }

    /// <inheritdoc />
    public Meter GetMeter(string? name = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _meter;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _meter.Dispose();
            _disposed = true;
        }
    }
}
