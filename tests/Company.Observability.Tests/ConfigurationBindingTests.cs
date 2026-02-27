using System.Collections.Generic;
using Company.Observability.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Company.Observability.Tests;

public sealed class ConfigurationBindingTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private static ObservabilityOptions BindFromJson(string json)
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var options = new ObservabilityOptions();
        config.GetSection(ObservabilityOptions.SectionName).Bind(options);
        return options;
    }

    private static ObservabilityOptions BindFromDictionary(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var options = new ObservabilityOptions();
        config.GetSection(ObservabilityOptions.SectionName).Bind(options);
        return options;
    }

    // ── Full JSON binding ────────────────────────────────────────────────────────────────

    [Fact]
    public void Bind_MapsAllTopLevelProperties_FromFullJson()
    {
        const string json = """
        {
          "Observability": {
            "ServiceName": "MyApi",
            "ServiceVersion": "2.1.0",
            "Environment": "Staging"
          }
        }
        """;

        var options = BindFromJson(json);

        options.ServiceName.Should().Be("MyApi");
        options.ServiceVersion.Should().Be("2.1.0");
        options.Environment.Should().Be("Staging");
    }

    [Fact]
    public void Bind_MapsLoggingSection_Correctly()
    {
        const string json = """
        {
          "Observability": {
            "ServiceName": "TestService",
            "Logging": {
              "Enabled": false,
              "MinimumLevel": "Warning",
              "SampleSuccessRate": 0.1,
              "EnableConsoleOutput": false,
              "CorrelationHeaderName": "X-Request-ID",
              "ExcludePaths": ["/ping", "/liveness"]
            }
          }
        }
        """;

        var options = BindFromJson(json);

        options.Logging.Enabled.Should().BeFalse();
        options.Logging.MinimumLevel.Should().Be("Warning");
        options.Logging.SampleSuccessRate.Should().Be(0.1);
        options.Logging.EnableConsoleOutput.Should().BeFalse();
        options.Logging.CorrelationHeaderName.Should().Be("X-Request-ID");
        // ConfigurationBinder appends to existing default arrays rather than replacing,
        // so we assert that the JSON-configured paths are present in the result.
        options.Logging.ExcludePaths.Should().Contain("/ping").And.Contain("/liveness");
    }

    [Fact]
    public void Bind_MapsGraylogSection_Correctly()
    {
        const string json = """
        {
          "Observability": {
            "ServiceName": "TestService",
            "Logging": {
              "Graylog": {
                "Enabled": true,
                "Host": "graylog.company.internal",
                "Port": 12202,
                "Protocol": "tcp",
                "Facility": "backend"
              }
            }
          }
        }
        """;

        var options = BindFromJson(json);

        options.Logging.Graylog.Enabled.Should().BeTrue();
        options.Logging.Graylog.Host.Should().Be("graylog.company.internal");
        options.Logging.Graylog.Port.Should().Be(12202);
        options.Logging.Graylog.Protocol.Should().Be("tcp");
        options.Logging.Graylog.Facility.Should().Be("backend");
    }

    [Fact]
    public void Bind_MapsTelemetrySection_Correctly()
    {
        const string json = """
        {
          "Observability": {
            "ServiceName": "TestService",
            "Telemetry": {
              "Enabled": true,
              "Tracing": {
                "Enabled": true,
                "SamplingRatio": 0.25,
                "SqlClientEnabled": true,
                "ExcludePaths": ["/ping"]
              },
              "Metrics": {
                "Enabled": true,
                "PrometheusEnabled": true
              },
              "Otlp": {
                "Enabled": true,
                "Endpoint": "http://collector:4317",
                "Protocol": "http/protobuf",
                "Headers": { "x-api-key": "mykey" }
              }
            }
          }
        }
        """;

        var options = BindFromJson(json);

        options.Telemetry.Enabled.Should().BeTrue();
        options.Telemetry.Tracing.SamplingRatio.Should().Be(0.25);
        options.Telemetry.Tracing.SqlClientEnabled.Should().BeTrue();
        options.Telemetry.Tracing.ExcludePaths.Should().Contain("/ping");
        options.Telemetry.Metrics.PrometheusEnabled.Should().BeTrue();
        options.Telemetry.Otlp.Enabled.Should().BeTrue();
        options.Telemetry.Otlp.Endpoint.Should().Be("http://collector:4317");
        options.Telemetry.Otlp.Protocol.Should().Be("http/protobuf");
        options.Telemetry.Otlp.Headers.Should().ContainKey("x-api-key");
        options.Telemetry.Otlp.Headers["x-api-key"].Should().Be("mykey");
    }

    [Fact]
    public void Bind_MapsRedactionSection_Correctly()
    {
        const string json = """
        {
          "Observability": {
            "ServiceName": "TestService",
            "Redaction": {
              "SensitiveKeys": ["nationalId", "taxCode"]
            }
          }
        }
        """;

        var options = BindFromJson(json);

        options.Redaction.SensitiveKeys.Should().Contain("nationalId");
        options.Redaction.SensitiveKeys.Should().Contain("taxCode");
    }

    // ── Default values ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bind_UsesCorrectDefaults_WhenSectionIsEmpty()
    {
        const string json = """{ "Observability": { "ServiceName": "X" } }""";

        var options = BindFromJson(json);

        options.Logging.Enabled.Should().BeTrue();
        options.Logging.MinimumLevel.Should().Be("Information");
        options.Logging.SampleSuccessRate.Should().Be(1.0);
        options.Logging.EnableConsoleOutput.Should().BeTrue();
        options.Logging.CorrelationHeaderName.Should().Be("X-Correlation-Id");
        options.Logging.ExcludePaths.Should().BeEquivalentTo(
            new[] { "/health", "/metrics", "/favicon.ico" });

        options.Logging.Graylog.Enabled.Should().BeFalse();
        options.Logging.Graylog.Port.Should().Be(12201);
        options.Logging.Graylog.Protocol.Should().Be("udp");

        options.Logging.RateLimit.Enabled.Should().BeTrue();
        options.Logging.RateLimit.MaxPerWindow.Should().Be(10);
        options.Logging.RateLimit.WindowSeconds.Should().Be(60);

        options.Telemetry.Enabled.Should().BeTrue();
        options.Telemetry.Tracing.Enabled.Should().BeTrue();
        options.Telemetry.Tracing.SamplingRatio.Should().Be(1.0);
        options.Telemetry.Tracing.SqlClientEnabled.Should().BeFalse();

        options.Telemetry.Metrics.Enabled.Should().BeTrue();
        options.Telemetry.Metrics.PrometheusEnabled.Should().BeFalse();

        options.Telemetry.Otlp.Enabled.Should().BeFalse();
        options.Telemetry.Otlp.Endpoint.Should().Be("http://localhost:4317");
        options.Telemetry.Otlp.Protocol.Should().Be("grpc");
    }

    // ── Environment variable overrides ────────────────────────────────────────────────────

    [Fact]
    public void Bind_SupportsEnvironmentVariableOverrides_ViaInMemoryProvider()
    {
        // Simulates: Observability__Logging__Graylog__Enabled=true
        var values = new Dictionary<string, string?>
        {
            ["Observability:ServiceName"] = "EnvOverrideService",
            ["Observability:Logging:Graylog:Enabled"] = "true",
            ["Observability:Logging:Graylog:Host"] = "graylog-prod",
            ["Observability:Telemetry:Otlp:Enabled"] = "true",
            ["Observability:Telemetry:Otlp:Endpoint"] = "http://otel:4317"
        };

        var options = BindFromDictionary(values);

        options.ServiceName.Should().Be("EnvOverrideService");
        options.Logging.Graylog.Enabled.Should().BeTrue();
        options.Logging.Graylog.Host.Should().Be("graylog-prod");
        options.Telemetry.Otlp.Enabled.Should().BeTrue();
        options.Telemetry.Otlp.Endpoint.Should().Be("http://otel:4317");
    }
}
