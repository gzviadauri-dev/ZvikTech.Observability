using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Company.Observability.Configuration;

/// <summary>
/// Root configuration options for Company.Observability.
/// Bind from the <c>"Observability"</c> section in <c>appsettings.json</c>.
/// </summary>
public sealed class ObservabilityOptions
{
    /// <summary>The configuration section name used for binding.</summary>
    public const string SectionName = "Observability";

    /// <summary>The logical service name used in logs and telemetry signals.</summary>
    [Required]
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>
    /// The service version string. Defaults to the entry-assembly informational version when not set.
    /// </summary>
    public string? ServiceVersion { get; set; }

    /// <summary>
    /// The deployment environment label (e.g. <c>Production</c>, <c>Staging</c>).
    /// Defaults to the value of the <c>ASPNETCORE_ENVIRONMENT</c> environment variable.
    /// </summary>
    public string? Environment { get; set; }

    /// <summary>Serilog logging pipeline configuration.</summary>
    public LoggingOptions Logging { get; set; } = new();

    /// <summary>OpenTelemetry tracing and metrics configuration.</summary>
    public TelemetryOptions Telemetry { get; set; } = new();

    /// <summary>PII / secrets redaction configuration.</summary>
    public RedactionOptions Redaction { get; set; } = new();
}

/// <summary>Controls the Serilog logging pipeline.</summary>
public sealed class LoggingOptions
{
    /// <summary>Whether the logging pipeline is active. Default: <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Minimum Serilog log level (Verbose | Debug | Information | Warning | Error | Fatal).
    /// Default: <c>"Information"</c>.
    /// </summary>
    public string MinimumLevel { get; set; } = "Information";

    /// <summary>
    /// Request paths excluded from request-level logging. Health and metrics endpoints are
    /// excluded by default to prevent noise in log aggregators.
    /// </summary>
    public string[] ExcludePaths { get; set; } = ["/health", "/metrics", "/favicon.ico"];

    /// <summary>
    /// Fraction of successful HTTP request log events to retain (0.0 – 1.0).
    /// <c>1.0</c> keeps every event; <c>0.1</c> keeps roughly 10 %.
    /// Warning, Error, and Fatal events are never dropped. Non-HTTP events always pass through.
    /// Default: <c>1.0</c>.
    /// </summary>
    public double SampleSuccessRate { get; set; } = 1.0;

    /// <summary>
    /// Whether to write structured JSON to <c>stdout</c>. Should remain <c>true</c> in
    /// containerised environments as the ultimate fallback sink.
    /// Default: <c>true</c>.
    /// </summary>
    public bool EnableConsoleOutput { get; set; } = true;

    /// <summary>
    /// The HTTP header name used to propagate / read the correlation identifier.
    /// Default: <c>"X-Correlation-Id"</c>.
    /// </summary>
    public string CorrelationHeaderName { get; set; } = "X-Correlation-Id";

    /// <summary>Graylog GELF sink configuration.</summary>
    public GraylogOptions Graylog { get; set; } = new();

    /// <summary>Rate-limiting filter for repeated Warning messages.</summary>
    public RateLimitOptions RateLimit { get; set; } = new();
}

/// <summary>Configuration for the Serilog Graylog GELF sink.</summary>
public sealed class GraylogOptions
{
    /// <summary>Whether to enable the Graylog sink. Default: <c>false</c>.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Graylog server hostname or IP address.
    /// Required when <see cref="Enabled"/> is <c>true</c>.
    /// </summary>
    public string? Host { get; set; }

    /// <summary>Graylog GELF input port. Default: <c>12201</c>.</summary>
    public int Port { get; set; } = 12201;

    /// <summary>Transport protocol: <c>"udp"</c> (default) or <c>"tcp"</c>.</summary>
    public string Protocol { get; set; } = "udp";

    /// <summary>Optional GELF facility / source name shown in Graylog search results.</summary>
    public string? Facility { get; set; }
}

/// <summary>Rate-limiting options for repeated log message templates.</summary>
public sealed class RateLimitOptions
{
    /// <summary>Whether the rate-limiting filter is active. Default: <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum occurrences of the same message template per window. Default: <c>10</c>.</summary>
    public int MaxPerWindow { get; set; } = 10;

    /// <summary>Rolling window size in seconds. Default: <c>60</c>.</summary>
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>Top-level OpenTelemetry telemetry configuration.</summary>
public sealed class TelemetryOptions
{
    /// <summary>Whether OpenTelemetry is enabled at all. Default: <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Distributed tracing configuration.</summary>
    public TracingOptions Tracing { get; set; } = new();

    /// <summary>Metrics configuration.</summary>
    public MetricsOptions Metrics { get; set; } = new();

    /// <summary>OTLP exporter configuration (shared by tracing and metrics).</summary>
    public OtlpOptions Otlp { get; set; } = new();
}

/// <summary>Distributed tracing configuration.</summary>
public sealed class TracingOptions
{
    /// <summary>Whether tracing is enabled. Default: <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Head-based sampling ratio (0.0 – 1.0).
    /// <c>1.0</c> = AlwaysOn; any other value uses <c>ParentBasedSampler</c> wrapping
    /// <c>TraceIdRatioBasedSampler</c>. Default: <c>1.0</c>.
    /// </summary>
    public double SamplingRatio { get; set; } = 1.0;

    /// <summary>
    /// Request paths excluded from trace instrumentation.
    /// Default: <c>["/health", "/metrics"]</c>.
    /// </summary>
    public string[] ExcludePaths { get; set; } = ["/health", "/metrics"];

    /// <summary>
    /// Whether to instrument SqlClient calls.
    /// Requires the <c>OpenTelemetry.Instrumentation.SqlClient</c> NuGet package — see the
    /// library <c>.csproj</c> for the commented-out reference and enable instructions.
    /// Default: <c>false</c>.
    /// </summary>
    public bool SqlClientEnabled { get; set; } = false;
}

/// <summary>Metrics collection configuration.</summary>
public sealed class MetricsOptions
{
    /// <summary>Whether metrics collection is enabled. Default: <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether to expose a Prometheus-compatible scraping endpoint at <c>/metrics</c>.
    /// Callers must also invoke <c>app.UseCompanyObservability()</c> which calls
    /// <c>app.MapPrometheusScrapingEndpoint("/metrics")</c>.
    /// Default: <c>false</c>.
    /// </summary>
    public bool PrometheusEnabled { get; set; } = false;
}

/// <summary>OTLP exporter configuration used by both tracing and metrics pipelines.</summary>
public sealed class OtlpOptions
{
    /// <summary>Whether to export via OTLP. Default: <c>false</c>.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>OTLP collector endpoint. Default: <c>"http://localhost:4317"</c>.</summary>
    public string Endpoint { get; set; } = "http://localhost:4317";

    /// <summary>
    /// Wire protocol: <c>"grpc"</c> (default) or <c>"http/protobuf"</c>.
    /// Use <c>"http/protobuf"</c> when the collector is behind an HTTP/1.1-only proxy.
    /// </summary>
    public string Protocol { get; set; } = "grpc";

    /// <summary>
    /// Optional key-value authentication headers sent with every OTLP request.
    /// Example: <c>{ "x-honeycomb-team": "YOUR_API_KEY" }</c>.
    /// These values are never written to application logs.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new();
}

/// <summary>PII / secrets redaction configuration.</summary>
public sealed class RedactionOptions
{
    /// <summary>
    /// Additional property names whose values will be replaced with <c>"***"</c>.
    /// Merged with the built-in default sensitive key list; does not replace it.
    /// </summary>
    public List<string> SensitiveKeys { get; set; } = new();
}
