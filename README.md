# Company.Observability

Production-ready observability for .NET 8 microservices — structured logging via
**Serilog → Graylog (GELF)**, distributed tracing and metrics via **OpenTelemetry
(OTLP → Jaeger + Prometheus)**, correlation IDs, PII redaction, head-based sampling,
and noise-control filters — wired in **two lines of code**.

---

## What it does

### Logs
Every log event is structured JSON enriched with `service.name`, `service.version`,
`deployment.environment`, `machine.name`, `process.id`, `thread.id`, W3C `trace_id`
and `span_id` (from `Activity.Current`), and the request `correlation_id` set by
`CorrelationMiddleware`. Sensitive property values (passwords, tokens, API keys, SSNs,
card numbers, and more) are automatically replaced with `***` before any event reaches
a sink. A configurable sampling filter keeps only a fraction of successful HTTP request
events in high-traffic environments, and a rate-limiting filter suppresses repeated
Warning messages after a configurable threshold per rolling window. All sinks are
wrapped in Serilog's async buffer (`bufferSize: 10_000, blockWhenFull: false`) so that
a slow or unavailable Graylog server never blocks request threads.

### Traces
Every inbound HTTP request, outbound HTTP call (via `HttpClient`), and custom span
(via `IActivitySourceFactory`) is emitted as an OpenTelemetry span. Exceptions are
recorded on the span automatically. A head-based sampler (AlwaysOn at ratio 1.0,
`ParentBasedSampler` + `TraceIdRatioBasedSampler` at any lower ratio) controls storage
volume. Health-check and metrics-scraping paths are excluded from tracing by default.
Spans are exported via OTLP gRPC to the OpenTelemetry Collector, which fans them out
to Jaeger.

### Metrics
ASP.NET Core, `HttpClient`, .NET Runtime, and process metrics are collected via
OpenTelemetry instrumentation. Custom application metrics are accessible through
`IMeterAccessor`. A Prometheus-compatible scraping endpoint (`/metrics`) can be
exposed in production; metrics are also exported via OTLP so the Collector can push
them to any backend (Prometheus, Grafana Cloud, Datadog, etc.).

---

## Architecture

```
                  ┌─────────────────────────────────────────────────┐
                  │              Your .NET 8 Service                 │
                  │                                                  │
                  │  Serilog pipeline:                               │
                  │    CorrelationMiddleware (sets trace+corr IDs)   │
                  │    → TraceEnricher (trace_id, span_id)           │
                  │    → RedactionEnricher (masks PII)               │
                  │    → SamplingFilter + RateLimitFilter            │
                  │    → WriteTo.Async ──┬──► Console (stdout JSON)  │
                  │                     └──► Graylog GELF UDP/TCP    │
                  │                                                  │
                  │  OpenTelemetry SDK:                              │
                  │    AspNetCore + HttpClient instrumentation        │
                  │    + Runtime/Process metrics                      │
                  │    + Custom spans (IActivitySourceFactory)        │
                  │    + Custom meters (IMeterAccessor)               │
                  └──────────────────┬──────────────────────────────┘
                                     │ OTLP gRPC :4317
                                     ▼
                        ┌────────────────────────┐
                        │   OpenTelemetry         │
                        │   Collector             │
                        │                         │
                        │  filter/health (drop)   │
                        │  → batch                │
                        └────┬───────────┬────────┘
                             │ OTLP      │ Prometheus
                             ▼           ▼  scrape :8888
                        ┌─────────┐  ┌──────────────┐
                        │ Jaeger  │  │  Prometheus   │
                        │  UI     │  │  + Grafana    │
                        │ :16686  │  │  :9090/:3000  │
                        └─────────┘  └──────────────┘

                  Serilog GELF ──► Graylog :12201 (UDP/TCP)
                                     │ backed by OpenSearch
                                     ▼
                               Graylog UI :9000
```

---

## How It Works

This section is a deep-dive into every stage of the observability pipeline — from the
first line of `Program.cs` to a log event appearing in Graylog and a span appearing in
Jaeger. Read it once to build a mental model that makes configuration, debugging, and
extension straightforward.

---

### 1. Startup Sequence

`AddCompanyObservability` and `UseCompanyObservability` split setup into two phases
that match the ASP.NET Core `WebApplicationBuilder` / `WebApplication` lifecycle:

```
Program.cs
    │
    ▼  builder.AddCompanyObservability()                 ← BEFORE builder.Build()
    │
    ├─ [1] Bind ObservabilityOptions from "Observability" config section
    │       └─ ValidateDataAnnotations() + ValidateOnStart()
    │           → crashes the app at startup if ServiceName is missing, Host is empty
    │              when Graylog is enabled, SamplingRatio is out of range, etc.
    │
    ├─ [2] Create BOOTSTRAP Serilog logger  (Log.Logger = CreateBootstrapLogger())
    │       └─ MinLevel=Warning overrides, Console sink only
    │           → captures any exception thrown during host.Build() itself
    │              (e.g. failed DI registration, options validation errors)
    │
    ├─ [3] Register builder.Host.UseSerilog() callback
    │       └─ This callback runs AFTER DI is fully built, giving access to IOptions<T>
    │           → Stage-2 logger replaces the bootstrap logger with full config:
    │              enrichers, Graylog sink, async wrapper, sampling + rate-limit filters
    │
    ├─ [4] Register OpenTelemetry SDK
    │       ├─ ResourceBuilder: service.name, service.version, deployment.environment,
    │       │   host.name, process.id
    │       ├─ TracerProviderBuilder: AspNetCore + HttpClient instrumentation,
    │       │   sampler, OTLP exporter (if enabled)
    │       └─ MeterProviderBuilder: AspNetCore + HttpClient + Runtime instrumentation,
    │           Prometheus exporter (if enabled), OTLP exporter (if enabled)
    │
    └─ [5] Register DI services
            CorrelationMiddleware (transient)
            IActivitySourceFactory  (singleton)
            IMeterAccessor          (singleton)
            IHttpContextAccessor    (singleton)
            ObservabilityOptionsValidator (singleton IValidateOptions<T>)

    │
    ▼  builder.Build()
    │   └─ IOptions<ObservabilityOptions> is now validated
    │   └─ UseSerilog callback fires → stage-2 logger becomes Log.Logger
    │
    ▼  app.UseCompanyObservability()                     ← AFTER builder.Build()
    │
    ├─ [6] app.UseMiddleware<CorrelationMiddleware>()    ← MUST be first
    ├─ [7] app.UseSerilogRequestLogging(...)             ← one structured log per request
    ├─ [8] app.MapPrometheusScrapingEndpoint("/metrics") ← only if PrometheusEnabled=true
    └─ [9] Register Log.CloseAndFlush() on ApplicationStopped
            → async buffer is fully drained before the process exits
```

---

### 2. Per-Request Lifecycle

The following shows every step that happens for a single inbound HTTP request, with the
component responsible at each stage:

```
Client sends:  GET /orders/42
               X-Correlation-Id: abc-123

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
 MIDDLEWARE PIPELINE (outermost → innermost)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

[1] CorrelationMiddleware (FIRST)
    ├── Reads "X-Correlation-Id: abc-123" from request headers
    │   └── If missing or whitespace → generates Guid.NewGuid().ToString("D")
    ├── CorrelationContext.CorrelationId = "abc-123"     ← AsyncLocal, flows via await
    ├── CorrelationContext.RequestId     = "abc-123"
    ├── LogContext.PushProperty("correlation_id", "abc-123")
    │   └── Every Serilog event downstream now carries correlation_id automatically
    ├── Response.OnStarting(() => Response.Headers["X-Correlation-Id"] = "abc-123")
    │   └── Header is added just before the response headers are flushed to wire
    └── Activity.Current is null?
        └── YES → starts a manual Activity "HTTP GET /orders/42" (ActivityKind.Server)
            └── NO → OTEL AspNetCore instrumentation has already started the span;
                     skip to avoid double-span

[2] SerilogRequestLoggingMiddleware  (UseSerilogRequestLogging)
    └── Will emit ONE structured log event AFTER the inner handler completes (see [6])

[3] OTEL AspNetCore Instrumentation (transparent, part of TracerProvider)
    ├── Creates span: "GET /orders/{id}"
    │   ├── trace_id = 0af7651916cd43dd8448eb...  (W3C 128-bit)
    │   ├── span_id  = b9c7c989f97918e1
    │   ├── http.method, http.route, http.status_code, user_agent, request.content_length
    │   └── Filters: path "/health" or "/metrics" → span NOT created
    └── Activity.Current is now set for the duration of this request

[4] Your endpoint handler (e.g. OrdersController or minimal-API lambda)
    ├── Calls logger.LogInformation("Processing order {OrderId}", 42)
    │   │
    │   └── Serilog enrichment chain:
    │       ├── FromLogContext()            → picks up correlation_id from [1]
    │       ├── WithProperty(service.name) → "OrdersApi"
    │       ├── WithProperty(deployment.environment) → "Production"
    │       ├── WithMachineName / WithProcessId / WithThreadId
    │       ├── TraceEnricher               → reads Activity.Current
    │       │   ├── trace_id = "0af7651916cd43dd8448eb..."  ← same as OTEL span!
    │       │   └── span_id  = "b9c7c989f97918e1"
    │       ├── CorrelationContextEnricher  → reads CorrelationContext.CorrelationId
    │       │   └── correlation_id = "abc-123"
    │       ├── RedactionEnricher           → scans ALL properties; masks PII keys
    │       ├── SamplingFilter              → HTTP event + SampleSuccessRate < 1.0?
    │       │   └── Drop if counter % modulo ≠ 0  (non-HTTP events always pass)
    │       └── RateLimitedLogFilter        → Warning template seen > MaxPerWindow?
    │           └── Drop if bucket exhausted (Error/Fatal always pass)
    │
    ├── (Optionally) uses IActivitySourceFactory to start a custom child span:
    │   using var span = factory.GetSource().StartActivity("ProcessOrder");
    │   span?.SetTag("order.id", orderId);
    │   └── This span is a CHILD of the OTEL AspNetCore span → appears nested in Jaeger
    │
    └── Calls IHttpClientFactory → outbound HTTP to another service
        └── OTEL HttpClient instrumentation adds W3C traceparent header automatically
            └── Downstream service reads the header → continues the same distributed trace

[5] Response returns up the middleware stack

[6] SerilogRequestLoggingMiddleware emits the synthetic request-log event:
    ├── Level selected by GetLevel delegate:
    │   ├── path in ExcludePaths ("/health") → Verbose  (suppressed / never written)
    │   ├── status 5xx or exception           → Error
    │   ├── status 4xx                        → Warning
    │   └── otherwise                         → Information
    └── Properties added by EnrichDiagnosticContext:
        http.method, http.route, http.status_code, http.scheme, client.ip,
        user_agent.original
        (These properties are what SamplingFilter checks to identify HTTP events)

[7] CorrelationMiddleware (on the way back out)
    ├── Calls CorrelationContext.Clear()   ← prevents AsyncLocal value leaking to
    │   other requests that reuse the same thread-pool thread
    └── Stops manual Activity (if one was started in [1])
```

---

### 3. The Serilog Enrichment & Filtering Pipeline

Every log event travels through stages in the exact order shown. An event that is
**dropped** by a filter never reaches the sinks.

```
LogEvent created
(logger.LogInformation / LogWarning / etc.)
        │
        ▼
┌───────────────────────────────────────────────────────────────────────────┐
│  ENRICHMENT STAGE                          (adds properties)              │
│                                                                           │
│  FromLogContext()          ← pulls properties pushed by middleware        │
│                              (correlation_id, request_id)                │
│  WithProperty(service.name)                                               │
│  WithProperty(service.version)                                            │
│  WithProperty(deployment.environment)                                     │
│  WithMachineName()                                                        │
│  WithProcessId()                                                          │
│  WithThreadId()                                                           │
│  TraceEnricher             ← reads Activity.Current (may be null)        │
│    ├─ trace_id: "0af7651916cd43dd8448eb211c80a429"  (32 hex chars, W3C)  │
│    └─ span_id:  "b9c7c989f97918e1"                  (16 hex chars, W3C)  │
│  CorrelationContextEnricher← reads CorrelationContext.CorrelationId       │
│    └─ correlation_id: "abc-123"                                           │
│  RedactionEnricher         ← case-insensitive scan of ALL property names  │
│    ├─ "password" → "***"                                                  │
│    ├─ "authorization" → "***"                                             │
│    └─ "http.request.headers.authorization" → "***"                       │
└───────────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌───────────────────────────────────────────────────────────────────────────┐
│  FILTER STAGE                              (drop or pass)                 │
│                                                                           │
│  SamplingFilter                                                           │
│    ├─ Level >= Warning?                → PASS (never dropped)             │
│    ├─ SampleSuccessRate == 1.0?        → PASS (no sampling)               │
│    ├─ No RequestPath / http.route?     → PASS (non-HTTP event)            │
│    └─ counter % ceil(1/rate) == 0?     → PASS, else DROP                  │
│                                                                           │
│  RateLimitedLogFilter                                                     │
│    ├─ Level >= Error?                  → PASS (never rate-limited)        │
│    ├─ Disabled?                        → PASS                             │
│    ├─ Template count in window ≤ Max?  → PASS                             │
│    └─ Count exceeded?                  → DROP                             │
└───────────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌───────────────────────────────────────────────────────────────────────────┐
│  ASYNC WRITE BUFFER (Serilog.Sinks.Async)                                 │
│    bufferSize: 10_000   blockWhenFull: false                              │
│    └─ Events are handed off to a background thread immediately            │
│       If the buffer is full → DROP (never blocks request threads)         │
└────────────────────────────┬──────────────────────────────────────────────┘
                             │ background thread
              ┌──────────────┴──────────────┐
              │                             │
              ▼                             ▼
   ┌──────────────────┐          ┌────────────────────────────┐
   │  Console sink     │          │  Graylog GELF sink          │
   │  (stdout, JSON)   │          │  (if Graylog.Enabled=true)  │
   │                   │          │                             │
   │  CompactJson fmt  │          │  UDP: fires-and-forgets     │
   │  Always on        │          │  TCP: persistent connection │
   │  Container-safe   │          │  try/catch → SelfLog.Write  │
   └──────────────────┘          └────────────────────────────┘
```

---

### 4. The OpenTelemetry Tracing Pipeline

```
Inbound HTTP request
        │
        ▼
┌──────────────────────────────────────────────────────────────────────────┐
│  SAMPLING DECISION (head-based, made once per trace root)                │
│                                                                           │
│  SamplingRatio == 1.0  → AlwaysOnSampler   (record everything)           │
│  SamplingRatio < 1.0   → ParentBasedSampler                              │
│    ├─ Has parent span with recorded decision?  → inherit parent decision  │
│    └─ Root span (no parent)?                  → TraceIdRatioBasedSampler  │
│        └─ Hash(traceId) % 1 < ratio?          → record, else drop         │
│                                                                           │
│  ⚠ Head-based sampling is decided at the ENTRY POINT.                    │
│    If a trace is dropped, ALL spans in that trace are dropped.           │
└──────────────────────────────────────────────────────────────────────────┘
        │ sampled = true
        ▼
┌──────────────────────────────────────────────────────────────────────────┐
│  SPAN CREATION & ENRICHMENT                                               │
│                                                                           │
│  AspNetCore instrumentation                                               │
│    ├─ span name: "GET /orders/{id}"                                       │
│    ├─ EnrichWithHttpRequest:  http.request.content_length,               │
│    │                          user_agent.original                         │
│    ├─ EnrichWithHttpResponse: http.response.content_length               │
│    ├─ RecordException=true    → exception.type, exception.message,       │
│    │                            exception.stacktrace on error             │
│    └─ Filter: path in ExcludePaths → span NOT created                    │
│                                                                           │
│  HttpClient instrumentation (for outbound calls)                          │
│    ├─ span name: "GET httpbin.org"                                        │
│    ├─ Injects W3C "traceparent" header into outbound request             │
│    │   traceparent: 00-{trace_id}-{span_id}-01                           │
│    ├─ RecordException=true                                                │
│    └─ Filter: skips /health and /metrics health-check calls              │
│                                                                           │
│  Custom spans (IActivitySourceFactory)                                    │
│    using var span = factory.GetSource().StartActivity("ProcessOrder");   │
│    span?.SetTag("order.id", "ORD-001");                                  │
│    span?.SetStatus(ActivityStatusCode.Ok);                               │
└──────────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌──────────────────────────────────────────────────────────────────────────┐
│  BATCH PROCESSOR                                                          │
│    Spans buffered in memory → flushed in batches to OTLP exporter        │
└──────────────────────────────────────────────────────────────────────────┘
        │ OTLP gRPC :4317
        ▼
┌──────────────────────────────────────────────────────────────────────────┐
│  OTEL COLLECTOR                                                           │
│    filter/health: drop spans where http.target == "/health" or "/metrics"│
│    batch processor: 1-second timeout, 512 spans/batch                    │
└──────────────────────────────────────────────────────────────────────────┘
        │ OTLP gRPC
        ▼
┌──────────────────────────────────────────────────────────────────────────┐
│  Jaeger                                                                   │
│    Stores spans in memory (all-in-one) or Cassandra/Elasticsearch (prod)  │
│    UI: search by service name, trace ID, operation name, tags            │
└──────────────────────────────────────────────────────────────────────────┘
```

---

### 5. The OpenTelemetry Metrics Pipeline

```
.NET runtime / ASP.NET Core / your code
emits measurements via System.Diagnostics.Metrics
        │
        ▼
┌──────────────────────────────────────────────────────────────────────────┐
│  INSTRUMENTATION LIBRARIES (auto-collect measurements)                    │
│                                                                           │
│  AddAspNetCoreInstrumentation                                             │
│    http.server.request.duration  (histogram, ms)                         │
│    http.server.active_requests   (updown counter)                        │
│                                                                           │
│  AddHttpClientInstrumentation                                             │
│    http.client.request.duration  (histogram, ms)                         │
│    http.client.active_requests   (updown counter)                        │
│                                                                           │
│  AddRuntimeInstrumentation                                                │
│    process.runtime.dotnet.gc.collections.count   (GC generations 0/1/2)  │
│    process.runtime.dotnet.jit.il_compiled.size                           │
│    process.runtime.dotnet.thread_pool.threads.count                      │
│    process.runtime.dotnet.heap.size (gen0, gen1, gen2, LOH)              │
│    process.runtime.dotnet.exceptions.count                               │
│    ... (30+ runtime metrics)                                             │
│                                                                           │
│  Custom meters (IMeterAccessor)                                           │
│    var counter = meterAccessor.GetMeter()                                │
│        .CreateCounter<long>("orders.processed");                         │
│    counter.Add(1, new TagList { {"order.type", "express"} });            │
└──────────────────────────────────────────────────────────────────────────┘
        │
        ├─────────────────────────────────────────────────┐
        │                                                 │
        ▼  (if Otlp.Enabled=true)                         ▼  (if PrometheusEnabled=true)
   OTLP gRPC → OTEL Collector → Prometheus              /metrics scraping endpoint
   (push model — app pushes periodically)               (pull model — Prometheus scrapes)
        │
        ▼
   Prometheus → Grafana dashboards
```

---

### 6. How the Three Signals Connect

The key property that ties logs, traces, and metrics together is **`trace_id`**. Because
`TraceEnricher` reads `Activity.Current` at log-event creation time — the same `Activity`
that the OTEL SDK uses for the current span — every log event that fires during a request
carries the exact same `trace_id` as the spans recorded by OTEL.

```
Single HTTP request "GET /orders/42"
│
├─ OTEL trace_id = "0af7651916cd43dd8448eb211c80a429"
│   spans: [GET /orders/{id}] → [ProcessOrder] → [GET httpbin.org]
│   visible in: Jaeger
│
├─ Serilog logs for this request
│   { "trace_id": "0af7651916cd43dd8448eb211c80a429",   ← same value
│     "correlation_id": "abc-123",
│     "service.name": "OrdersApi",
│     "http.route": "/orders/{id}",
│     "Message": "Processing order 42" }
│   visible in: Graylog
│
└─ Metrics for this request contribute to
   http.server.request.duration {http.route="/orders/{id}", http.status_code=200}
   visible in: Prometheus / Grafana
```

**Jumping between signals:**

| Starting point | Where to look next | How |
|---|---|---|
| Graylog log event | Jaeger trace | Copy `trace_id` from the log → paste into Jaeger search |
| Jaeger span | Graylog logs | Copy `traceId` from span tags → search `trace_id:"..."` in Graylog |
| Slow Grafana panel | Jaeger traces | Note the time window → open Jaeger, filter by service + time |
| Client error report | Graylog | Client returns `X-Correlation-Id` header → search `correlation_id:"..."` |

---

### 7. Two-Stage Serilog Initialisation — Why It Matters

ASP.NET Core builds the DI container inside `builder.Build()`. If anything fails there
(missing configuration, failed `ValidateOnStart`, bad DI registration), the exception
would go unlogged without a pre-configured logger.

```
Stage 1 — Bootstrap logger (no DI, runs before Build())
┌─────────────────────────────────────────────────────┐
│  Log.Logger = new LoggerConfiguration()             │
│      .MinimumLevel.Override("Microsoft", Warning)   │
│      .Enrich.FromLogContext()                        │
│      .WriteTo.Console(CompactJsonFormatter)          │
│      .CreateBootstrapLogger()                        │
│                                                      │
│  ✓ Captures: DI registration errors                 │
│  ✓ Captures: ValidateOnStart option failures         │
│  ✓ Captures: appsettings.json parse errors           │
│  ✗ Missing:  Graylog sink (no config yet)            │
│  ✗ Missing:  trace_id enrichment (no Activity)       │
└─────────────────────────────────────────────────────┘
        │
        ▼  builder.Build() completes successfully
        │
Stage 2 — Full DI-aware logger (runs inside UseSerilog callback)
┌─────────────────────────────────────────────────────┐
│  builder.Host.UseSerilog((ctx, services, cfg) => {  │
│      var opts = services                             │
│          .GetRequiredService<IOptions<...>>().Value; │
│      ConfigureFullLogger(cfg, opts, ctx.Config);     │
│  });                                                 │
│                                                      │
│  ✓ Full enrichment pipeline (TraceEnricher, etc.)    │
│  ✓ Graylog GELF sink (if enabled)                    │
│  ✓ Sampling + rate-limit filters                     │
│  ✓ ReadFrom.Configuration() for level overrides      │
│  ✓ Async buffer wrapping all sinks                   │
└─────────────────────────────────────────────────────┘
```

---

### 8. Class Responsibilities Quick Reference

| Class / Interface | Layer | Responsibility |
|---|---|---|
| `ObservabilityExtensions` | Entry point | `AddCompanyObservability` / `UseCompanyObservability` — wires everything in 2 lines |
| `ObservabilityOptions` | Config | Root options tree; binds from `"Observability"` section |
| `ObservabilityOptionsValidator` | Config | `IValidateOptions<T>` — enforces required fields at startup |
| `LoggingServiceExtensions` | Logging | Two-stage Serilog init; `ConfigureRequestLogging` |
| `CorrelationMiddleware` | Logging | First middleware; reads/generates correlation ID, pushes to `LogContext` and `AsyncLocal` |
| `CorrelationContext` | Logging | `AsyncLocal<string?>` store; accessible anywhere in the call chain including background tasks |
| `TraceEnricher` | Logging | Reads `Activity.Current` → adds `trace_id` / `span_id` to every log event |
| `RedactionEnricher` | Logging | Scans all property names; replaces sensitive values with `"***"` |
| `SamplingFilter` | Logging | Probabilistically drops HTTP request log events; passes all non-HTTP and Warning+ events |
| `RateLimitedLogFilter` | Logging | Suppresses repeated Warning messages after `MaxPerWindow` per rolling window |
| `TelemetryServiceExtensions` | Telemetry | Registers OTEL SDK: resources, instrumentation, sampler, exporters |
| `ActivitySourceFactory` | Telemetry | Provides a named `ActivitySource` for custom span creation |
| `MeterAccessor` | Telemetry | Provides a named `Meter` for custom metric creation |

---

## Quick Start

### 1. Install

```xml
<PackageReference Include="Company.Observability" Version="1.0.0" />
```

### 2. Configure (two lines in `Program.cs`)

```csharp
var builder = WebApplication.CreateBuilder(args);

// Before builder.Build() — registers Serilog + OpenTelemetry + correlation
builder.AddCompanyObservability();

var app = builder.Build();

// After builder.Build() — adds correlation middleware + request logging + /metrics
app.UseCompanyObservability();

app.Run();
```

### 3. Add `appsettings.json` section

```jsonc
{
  "Observability": {
    "ServiceName": "MyApi",
    "ServiceVersion": "1.0.0",
    "Logging": {
      "MinimumLevel": "Information",
      "Graylog": {
        "Enabled": false,
        "Host": "graylog.internal",
        "Port": 12201,
        "Protocol": "udp"
      }
    },
    "Telemetry": {
      "Otlp": {
        "Enabled": false,
        "Endpoint": "http://otel-collector:4317"
      }
    }
  }
}
```

---

## Full `appsettings.json` Reference

```jsonc
{
  "Observability": {

    // [Required] Logical service name. Appears in all logs, traces, and metrics.
    "ServiceName": "MyApi",

    // Service version. Defaults to entry-assembly informational version.
    "ServiceVersion": "1.0.0",

    // Deployment environment label. Defaults to ASPNETCORE_ENVIRONMENT.
    "Environment": "Development",

    "Logging": {
      "Enabled": true,

      // Minimum Serilog level: Verbose|Debug|Information|Warning|Error|Fatal
      "MinimumLevel": "Information",

      // Paths excluded from per-request log entries (logged at Verbose = silent).
      "ExcludePaths": ["/health", "/metrics", "/favicon.ico"],

      // Log sampling for successful HTTP requests (0.0–1.0). Non-HTTP events always pass.
      "SampleSuccessRate": 1.0,

      // Always write compact JSON to stdout (container-friendly fallback).
      "EnableConsoleOutput": true,

      // HTTP header name for correlation ID propagation.
      "CorrelationHeaderName": "X-Correlation-Id",

      "Graylog": {
        "Enabled": false,
        "Host": "graylog.internal",  // required when Enabled=true
        "Port": 12201,
        "Protocol": "udp",           // "udp" or "tcp" (tcp recommended for prod)
        "Facility": "MyApi"
      },

      "RateLimit": {
        "Enabled": true,
        "MaxPerWindow": 10,          // max Warning occurrences per window
        "WindowSeconds": 60
      }
    },

    "Telemetry": {
      "Enabled": true,

      "Tracing": {
        "Enabled": true,
        "SamplingRatio": 1.0,        // 1.0=AlwaysOn; <1.0=ParentBased+TraceIdRatio
        "ExcludePaths": ["/health", "/metrics"],
        "SqlClientEnabled": false    // requires extra NuGet package (see csproj)
      },

      "Metrics": {
        "Enabled": true,
        "PrometheusEnabled": false   // exposes /metrics; call UseCompanyObservability()
      },

      "Otlp": {
        "Enabled": false,
        "Endpoint": "http://otel-collector:4317",
        "Protocol": "grpc",          // "grpc" or "http/protobuf"
        "Headers": {}                // optional: { "x-api-key": "..." } — never logged
      }
    },

    "Redaction": {
      // Merged with built-in defaults; does not replace them.
      "SensitiveKeys": ["nationalId", "taxCode"]
    }
  }
}
```

---

## How Correlation Works End-to-End

```
Inbound HTTP request
        │
        ▼
CorrelationMiddleware
  ├── Reads X-Correlation-Id header (or generates a new UUID)
  ├── Sets CorrelationContext.CorrelationId  (AsyncLocal — flows through await)
  ├── Calls LogContext.PushProperty("correlation_id", id)
  └── Schedules Response.OnStarting → adds X-Correlation-Id header to response

Every Serilog event on this request now carries:
  { "correlation_id": "abc123", "trace_id": "0af7651...", "span_id": "00f067aa..." }

In Graylog:
  Search:  correlation_id:"abc123"
  → See ALL log events (controller logs, service logs, SQL logs) for this request.

In Jaeger:
  Copy trace_id from the Graylog log entry → paste into Jaeger search
  → Navigate directly to the exact distributed trace.

Response to client:
  X-Correlation-Id: abc123
  → Client includes this in the next call so you can correlate multi-step flows.
```

---

## PII Redaction

The `RedactionEnricher` replaces property values with `***` for these keys by default:

| Key category     | Keys redacted (case-insensitive)                                       |
|------------------|------------------------------------------------------------------------|
| Auth / secrets   | `password`, `passwd`, `secret`, `token`, `authorization`, `auth`       |
| API credentials  | `apikey`, `api_key`, `clientsecret`, `client_secret`, `privatekey`, `private_key` |
| Connection info  | `connectionstring`, `connection_string`, `credential`, `credentials`   |
| PII              | `personalid`, `ssn`, `cardnumber`, `card_number`, `cvv`, `pin`         |
| HTTP headers     | Any `http.request.headers.{sensitive-key}` (e.g. `authorization`)      |

### Adding custom keys

```jsonc
"Observability": {
  "Redaction": {
    "SensitiveKeys": ["nationalId", "taxRegistrationNumber", "iban"]
  }
}
```

Custom keys are **merged** with the defaults — they do not replace them.

---

## Production Recommendations

| Setting                            | Development | Production       | Reason                                           |
|------------------------------------|-------------|------------------|--------------------------------------------------|
| `SampleSuccessRate`                | `1.0`       | `0.05` – `0.10`  | Reduce Graylog ingestion cost by 90–95 %         |
| `Tracing:SamplingRatio`            | `1.0`       | `0.05` – `0.10`  | Reduce Jaeger storage; parent spans are kept      |
| `RateLimit:Enabled`                | `true`      | `true`           | Prevent Warning floods from filling Graylog      |
| `Graylog:Protocol`                 | `udp`       | `tcp`            | TCP is reliable; UDP silently drops large events |
| `Logging:EnableConsoleOutput`      | `true`      | `true`           | Keep as container stdout backup                  |
| `Async blockWhenFull`              | n/a         | `false`          | Drop logs vs block request threads under pressure |
| `Otlp:Enabled`                     | `false`     | `true`           | Export to Collector for Jaeger + Prometheus      |

---

## Running Locally with Docker Compose

### Prerequisites
- Docker Desktop 4.x+
- Ports 9000, 12201, 4317, 4318, 16686, 9090, 3000, 8080 available

### 1. Start the stack

```bash
docker-compose up -d
```

### 2. Set up a Graylog GELF input

1. Open [http://localhost:9000](http://localhost:9000) — login: `admin` / `admin`
2. Go to **System → Inputs**
3. Select **GELF UDP** → **Launch new input**
4. Set port `12201`, click **Save**
5. Repeat for **GELF TCP** if using `Protocol=tcp`

### 3. Hit the sample API

```bash
# Trigger a traced request
curl http://localhost:8080/hello?name=World

# Trigger a child span (distributed tracing demo)
curl http://localhost:8080/outbound

# Trigger an error (recorded on the span + logged at Error)
curl http://localhost:8080/error

# Create a custom span
curl -X POST http://localhost:8080/orders \
     -H "Content-Type: application/json" \
     -d '{"customerId":"cust-1","items":["SKU-001","SKU-002"]}'
```

### 4. View signals

| Tool       | URL                         | What to look for                                   |
|------------|-----------------------------|----------------------------------------------------|
| Graylog    | http://localhost:9000       | Search `service.name:SampleApi`, filter by level   |
| Jaeger     | http://localhost:16686      | Service = `SampleApi`, trace the `/orders` span    |
| Prometheus | http://localhost:9090       | Query `http_server_duration_ms_bucket`             |
| Grafana    | http://localhost:3000       | Add Prometheus + Jaeger data sources               |
| Swagger    | http://localhost:8080/swagger | Interactive API docs                             |

---

## Publishing to NuGet

Pushes happen automatically via GitHub Actions when you create a version tag:

```bash
git tag v1.0.0
git push origin v1.0.0
```

The `publish` job (environment: `nuget-release`) requires a repository secret
`NUGET_API_KEY` with a NuGet.org API key that has push permission for
`Company.Observability`. Both the `.nupkg` (library) and `.snupkg` (source symbols)
are published so that debuggers can step into the package source.

Preview packages are created automatically on every push to `main` or `develop` with
version `1.0.0-preview.{run_number}+{sha}`.

---

## FAQ

### Graylog is not receiving log events

1. Confirm the GELF input is **Running** in Graylog UI → System → Inputs.
2. Verify `Observability:Logging:Graylog:Enabled = true` in your config.
3. Check `Host` and `Port` — when running in Docker, use the service name `graylog`, not `localhost`.
4. Check Serilog's self-log: add `Serilog.Debugging.SelfLog.Enable(Console.Error)` before
   `builder.AddCompanyObservability()` — errors from the Graylog sink appear there.
5. For large messages try switching `Protocol` to `tcp` — UDP datagrams are silently dropped
   when they exceed the network MTU.

### Traces are not appearing in Jaeger

1. Verify `Observability:Telemetry:Otlp:Enabled = true`.
2. Confirm the `Endpoint` points to the **OTEL Collector** (not directly to Jaeger):
   `http://otel-collector:4317` in Docker, `http://localhost:4317` locally.
3. Check the collector logs: `docker-compose logs otel-collector`. Look for
   `"Dropped spans"` or connection errors.
4. The OTEL Collector must be running before the app exports. Add `depends_on` in
   `docker-compose.yml` if the collector starts slowly.
5. Health endpoints are **filtered** by the collector pipeline. Use `/hello` or
   `/orders` instead of `/health` when testing.

### Prometheus is not scraping metrics

1. Ensure `PrometheusEnabled = true` **and** `UseCompanyObservability()` is called.
2. Confirm `prometheus.yml` has the correct `targets` for your service.
3. Check Prometheus targets at http://localhost:9090/targets — the service must show
   `State: UP`.

### `ValidateOnStart` fails at startup with option errors

The library calls `ValidateOnStart()` for `ObservabilityOptions`. Validation failures
are intentional — they protect you from running with broken observability config.
Common fixes:
- If `Graylog.Enabled=true`, ensure `Graylog.Host` is non-empty.
- If `Otlp.Enabled=true`, ensure `Otlp.Endpoint` is non-empty.
- Sampling ratios must be between 0.0 and 1.0.

### OpenTelemetry.Exporter.Prometheus.AspNetCore stability

This package is currently at `1.9.0-rc.1` (Release Candidate). The API is stable for
the purposes of this library but the package may emit deprecation warnings in future
SDK versions. Monitor the [OpenTelemetry .NET releases](https://github.com/open-telemetry/opentelemetry-dotnet/releases)
for GA versions.

---

## License

MIT — see [LICENSE](LICENSE) for details.
