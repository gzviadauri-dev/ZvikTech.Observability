using System;
using System.Collections.Generic;
using System.Diagnostics;
using Company.Observability.Logging;
using FluentAssertions;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Company.Observability.Tests;

public sealed class TraceEnricherTests : IDisposable
{
    private readonly ActivitySource _activitySource;
    private readonly ActivityListener _listener;

    public TraceEnricherTests()
    {
        _activitySource = new ActivitySource("TraceEnricherTests.Source");

        // Register a listener that accepts all activities so StartActivity() is not a no-op.
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _activitySource.Dispose();
        _listener.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private static LogEvent CreateEmptyEvent() =>
        new(DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            messageTemplate: new MessageTemplateParser().Parse("Test"),
            properties: new List<LogEventProperty>());

    private static string? GetScalarValue(LogEvent e, string key)
    {
        return e.Properties.TryGetValue(key, out var v) && v is ScalarValue sv
            ? sv.Value?.ToString()
            : null;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Enrich_AddsTraceIdAndSpanId_WhenActivityIsActive()
    {
        using var activity = _activitySource.StartActivity("TestSpan");
        activity.Should().NotBeNull("the listener must accept the activity");

        var enricher = new TraceEnricher();
        var logEvent = CreateEmptyEvent();

        enricher.Enrich(logEvent, new TestPropertyFactory());

        logEvent.Properties.Should().ContainKey("trace_id");
        logEvent.Properties.Should().ContainKey("span_id");
    }

    [Fact]
    public void Enrich_TraceId_Is32LowerHexChars_W3CFormat()
    {
        using var activity = _activitySource.StartActivity("TestSpan");
        activity.Should().NotBeNull();

        var enricher = new TraceEnricher();
        var logEvent = CreateEmptyEvent();
        enricher.Enrich(logEvent, new TestPropertyFactory());

        var traceId = GetScalarValue(logEvent, "trace_id");
        traceId.Should().NotBeNullOrEmpty();
        traceId!.Length.Should().Be(32, "W3C trace ID is 128 bits = 32 lower-case hex chars");
        traceId.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public void Enrich_SpanId_Is16LowerHexChars()
    {
        using var activity = _activitySource.StartActivity("TestSpan");
        activity.Should().NotBeNull();

        var enricher = new TraceEnricher();
        var logEvent = CreateEmptyEvent();
        enricher.Enrich(logEvent, new TestPropertyFactory());

        var spanId = GetScalarValue(logEvent, "span_id");
        spanId.Should().NotBeNullOrEmpty();
        spanId!.Length.Should().Be(16, "W3C span ID is 64 bits = 16 lower-case hex chars");
        spanId.Should().MatchRegex("^[0-9a-f]{16}$");
    }

    [Fact]
    public void Enrich_TraceIdMatchesCurrentActivity()
    {
        using var activity = _activitySource.StartActivity("TestSpan");
        activity.Should().NotBeNull();

        var expectedTraceId = Activity.Current!.TraceId.ToString();

        var enricher = new TraceEnricher();
        var logEvent = CreateEmptyEvent();
        enricher.Enrich(logEvent, new TestPropertyFactory());

        GetScalarValue(logEvent, "trace_id").Should().Be(expectedTraceId);
    }

    [Fact]
    public void Enrich_SkipsGracefully_WhenNoActivityIsActive()
    {
        // Ensure no ambient activity.
        Activity.Current.Should().BeNull("this test requires no active activity");

        var enricher = new TraceEnricher();
        var logEvent = CreateEmptyEvent();

        var act = () => enricher.Enrich(logEvent, new TestPropertyFactory());
        act.Should().NotThrow();

        logEvent.Properties.Should().NotContainKey("trace_id");
        logEvent.Properties.Should().NotContainKey("span_id");
    }

    [Fact]
    public void Enrich_DoesNotThrow_WhenCalledMultipleTimes()
    {
        using var activity = _activitySource.StartActivity("TestSpan");
        var enricher = new TraceEnricher();
        var logEvent = CreateEmptyEvent();

        var act = () =>
        {
            enricher.Enrich(logEvent, new TestPropertyFactory());
            enricher.Enrich(logEvent, new TestPropertyFactory());
        };

        act.Should().NotThrow();
    }

    // ── Test helper ───────────────────────────────────────────────────────────────────────

    private sealed class TestPropertyFactory : Serilog.Core.ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }
}
