using Company.Observability.Configuration;
using FluentAssertions;
using Xunit;

namespace Company.Observability.Tests;

public sealed class ObservabilityOptionsValidatorTests
{
    private static ObservabilityOptionsValidator CreateValidator() =>
        new();

    private static ObservabilityOptions ValidOptions() =>
        new()
        {
            ServiceName = "MyService",
            Logging = new LoggingOptions
            {
                SampleSuccessRate = 1.0,
                Graylog = new GraylogOptions { Enabled = false },
                RateLimit = new RateLimitOptions { MaxPerWindow = 10, WindowSeconds = 60 }
            },
            Telemetry = new TelemetryOptions
            {
                Tracing = new TracingOptions { SamplingRatio = 1.0 },
                Otlp = new OtlpOptions { Enabled = false }
            }
        };

    // ── Passing cases ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_Passes_WithFullyValidConfiguration()
    {
        var result = CreateValidator().Validate(null, ValidOptions());
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_Passes_WhenGraylogDisabled_AndHostIsEmpty()
    {
        var options = ValidOptions();
        options.Logging.Graylog.Enabled = false;
        options.Logging.Graylog.Host = null;

        var result = CreateValidator().Validate(null, options);
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_Passes_WhenOtlpDisabled_AndEndpointIsEmpty()
    {
        var options = ValidOptions();
        options.Telemetry.Otlp.Enabled = false;
        options.Telemetry.Otlp.Endpoint = string.Empty;

        var result = CreateValidator().Validate(null, options);
        result.Succeeded.Should().BeTrue();
    }

    // ── Graylog validation ────────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_Fails_WhenGraylogEnabled_AndHostIsEmpty()
    {
        var options = ValidOptions();
        options.Logging.Graylog.Enabled = true;
        options.Logging.Graylog.Host = string.Empty;

        var result = CreateValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Graylog");
        result.FailureMessage.Should().Contain("Host");
    }

    [Fact]
    public void Validate_Fails_WhenGraylogEnabled_AndHostIsWhitespace()
    {
        var options = ValidOptions();
        options.Logging.Graylog.Enabled = true;
        options.Logging.Graylog.Host = "   ";

        var result = CreateValidator().Validate(null, options);
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_Passes_WhenGraylogEnabled_AndHostIsProvided()
    {
        var options = ValidOptions();
        options.Logging.Graylog.Enabled = true;
        options.Logging.Graylog.Host = "graylog.internal";

        var result = CreateValidator().Validate(null, options);
        result.Succeeded.Should().BeTrue();
    }

    // ── OTLP validation ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_Fails_WhenOtlpEnabled_AndEndpointIsEmpty()
    {
        var options = ValidOptions();
        options.Telemetry.Otlp.Enabled = true;
        options.Telemetry.Otlp.Endpoint = string.Empty;

        var result = CreateValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Otlp");
        result.FailureMessage.Should().Contain("Endpoint");
    }

    [Fact]
    public void Validate_Passes_WhenOtlpEnabled_AndEndpointIsProvided()
    {
        var options = ValidOptions();
        options.Telemetry.Otlp.Enabled = true;
        options.Telemetry.Otlp.Endpoint = "http://otel-collector:4317";

        var result = CreateValidator().Validate(null, options);
        result.Succeeded.Should().BeTrue();
    }

    // ── SamplingRatio validation ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-0.1)]
    [InlineData(-1.0)]
    [InlineData(1.1)]
    [InlineData(2.0)]
    public void Validate_Fails_WhenSamplingRatioIsOutOfRange(double ratio)
    {
        var options = ValidOptions();
        options.Telemetry.Tracing.SamplingRatio = ratio;

        var result = CreateValidator().Validate(null, options);
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("SamplingRatio");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Validate_Passes_WhenSamplingRatioIsInRange(double ratio)
    {
        var options = ValidOptions();
        options.Telemetry.Tracing.SamplingRatio = ratio;

        var result = CreateValidator().Validate(null, options);
        result.Succeeded.Should().BeTrue();
    }

    // ── SampleSuccessRate validation ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Validate_Fails_WhenSampleSuccessRateIsOutOfRange(double rate)
    {
        var options = ValidOptions();
        options.Logging.SampleSuccessRate = rate;

        var result = CreateValidator().Validate(null, options);
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("SampleSuccessRate");
    }

    // ── Multiple failures ────────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_ReturnsMultipleFailures_WhenMultipleRulesViolated()
    {
        var options = ValidOptions();
        options.Logging.Graylog.Enabled = true;
        options.Logging.Graylog.Host = null;
        options.Telemetry.Otlp.Enabled = true;
        options.Telemetry.Otlp.Endpoint = string.Empty;

        var result = CreateValidator().Validate(null, options);
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Graylog");
        result.FailureMessage.Should().Contain("Otlp");
    }
}
