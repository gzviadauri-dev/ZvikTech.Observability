using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Company.Observability.Configuration;

/// <summary>
/// Validates <see cref="ObservabilityOptions"/> at application startup so that
/// mis-configurations are detected early rather than at first use.
/// </summary>
/// <remarks>
/// Registered automatically by <c>AddCompanyObservability</c>.
/// Failures surface as a fatal startup exception via <c>ValidateOnStart()</c>.
/// </remarks>
internal sealed class ObservabilityOptionsValidator : IValidateOptions<ObservabilityOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ObservabilityOptions options)
    {
        var errors = new List<string>();

        // Graylog: Host is required when the sink is enabled.
        if (options.Logging.Graylog.Enabled &&
            string.IsNullOrWhiteSpace(options.Logging.Graylog.Host))
        {
            errors.Add("Observability:Logging:Graylog:Host is required when Graylog.Enabled = true.");
        }

        // OTLP: Endpoint is required when the exporter is enabled.
        if (options.Telemetry.Otlp.Enabled &&
            string.IsNullOrWhiteSpace(options.Telemetry.Otlp.Endpoint))
        {
            errors.Add("Observability:Telemetry:Otlp:Endpoint is required when Otlp.Enabled = true.");
        }

        // Tracing sampling ratio must be a valid probability.
        if (options.Telemetry.Tracing.SamplingRatio < 0.0 ||
            options.Telemetry.Tracing.SamplingRatio > 1.0)
        {
            errors.Add($"Observability:Telemetry:Tracing:SamplingRatio must be between 0 and 1 " +
                       $"(current value: {options.Telemetry.Tracing.SamplingRatio}).");
        }

        // Log sampling rate must be a valid probability.
        if (options.Logging.SampleSuccessRate < 0.0 ||
            options.Logging.SampleSuccessRate > 1.0)
        {
            errors.Add($"Observability:Logging:SampleSuccessRate must be between 0 and 1 " +
                       $"(current value: {options.Logging.SampleSuccessRate}).");
        }

        // Rate-limit window must be positive.
        if (options.Logging.RateLimit.WindowSeconds <= 0)
        {
            errors.Add("Observability:Logging:RateLimit:WindowSeconds must be greater than 0.");
        }

        // Rate-limit max per window must be positive.
        if (options.Logging.RateLimit.MaxPerWindow <= 0)
        {
            errors.Add("Observability:Logging:RateLimit:MaxPerWindow must be greater than 0.");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
