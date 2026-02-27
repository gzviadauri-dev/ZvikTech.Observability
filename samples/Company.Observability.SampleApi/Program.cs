using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using Company.Observability;
using Company.Observability.Logging;
using Company.Observability.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ── Bootstrap ────────────────────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

// One-line observability setup: Serilog + OpenTelemetry + correlation + redaction.
builder.AddCompanyObservability();

builder.Services.AddHttpClient();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();

var app = builder.Build();

// One-line middleware pipeline: correlation middleware + request logging + Prometheus.
app.UseCompanyObservability();

app.UseSwagger();
app.UseSwaggerUI();
app.UseExceptionHandler();

// ── Endpoints ────────────────────────────────────────────────────────────────────────────

// GET /health — excluded from request logging and tracing by default.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .WithName("Health")
    .WithOpenApi();

// GET /hello?name=World — demonstrates trace_id correlation end-to-end.
app.MapGet("/hello", (
    ILogger<Program> logger,
    string? name = "World") =>
{
    var displayName = name ?? "World";
    logger.LogInformation("Saying hello to {Name}", displayName);

    return Results.Ok(new
    {
        message = $"Hello, {displayName}!",
        correlationId = CorrelationContext.CorrelationId,
        traceId = Activity.Current?.TraceId.ToString()
    });
})
.WithName("Hello")
.WithOpenApi();

// GET /outbound — demonstrates distributed tracing via HttpClient instrumentation.
app.MapGet("/outbound", async (
    IHttpClientFactory httpClientFactory,
    ILogger<Program> logger) =>
{
    using var client = httpClientFactory.CreateClient();

    HttpResponseMessage? response = null;
    try
    {
        response = await client.GetAsync("https://httpbin.org/get");
        logger.LogInformation("Upstream httpbin.org responded with {StatusCode}",
            (int)response.StatusCode);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Outbound HTTP call to httpbin.org failed");
    }

    return Results.Ok(new
    {
        upstreamStatus = response is not null ? (int)response.StatusCode : 0,
        traceId = Activity.Current?.TraceId.ToString(),
        correlationId = CorrelationContext.CorrelationId
    });
})
.WithName("Outbound")
.WithOpenApi();

// GET /error — demonstrates exception capture by OTEL tracing and Serilog.
app.MapGet("/error", (ILogger<Program> logger) =>
{
    logger.LogWarning("About to throw an intentional demo error");
    throw new InvalidOperationException("Intentional demo error — this is expected.");
})
.WithName("Error")
.WithOpenApi();

// POST /orders — demonstrates custom ActivitySource spans with business tags.
app.MapPost("/orders", (
    CreateOrderRequest request,
    IActivitySourceFactory activitySourceFactory,
    ILogger<Program> logger) =>
{
    using var activity = activitySourceFactory
        .GetSource()
        .StartActivity("ProcessOrder", ActivityKind.Internal);

    activity?.SetTag("order.customer_id", request.CustomerId);
    activity?.SetTag("order.item_count", request.Items?.Count ?? 0);

    logger.LogInformation(
        "Processing order for customer {CustomerId} with {ItemCount} item(s)",
        request.CustomerId,
        request.Items?.Count ?? 0);

    // Simulate work.
    var orderId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    activity?.SetTag("order.id", orderId);
    activity?.SetStatus(ActivityStatusCode.Ok);

    return Results.Created($"/orders/{orderId}", new
    {
        orderId,
        customerId = request.CustomerId,
        items = request.Items,
        status = "Accepted",
        traceId = Activity.Current?.TraceId.ToString()
    });
})
.WithName("CreateOrder")
.WithOpenApi();

app.Run();

// ── Request/response models ───────────────────────────────────────────────────────────────

/// <summary>Request body for the POST /orders endpoint.</summary>
public sealed record CreateOrderRequest(
    [property: System.ComponentModel.DataAnnotations.Required] string CustomerId,
    List<string>? Items);

// Make Program accessible from test projects.
public partial class Program { }
