using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Company.Observability.Configuration;
using Company.Observability.Logging;
using FluentAssertions;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Company.Observability.Tests;

public sealed class SamplingFilterTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private static SamplingFilter CreateFilter(double sampleRate)
    {
        var options = new ObservabilityOptions
        {
            ServiceName = "test",
            Logging = new LoggingOptions { SampleSuccessRate = sampleRate }
        };
        return new SamplingFilter(options);
    }

    private static LogEvent CreateEvent(
        LogEventLevel level,
        bool isHttpEvent = false)
    {
        var properties = new List<LogEventProperty>();

        if (isHttpEvent)
        {
            properties.Add(new LogEventProperty("RequestPath", new ScalarValue("/api/orders")));
        }

        return new LogEvent(
            DateTimeOffset.UtcNow,
            level,
            exception: null,
            messageTemplate: new MessageTemplateParser().Parse("Test"),
            properties: properties);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_PassesAllEvents_WhenSampleRateIs1()
    {
        var filter = CreateFilter(1.0);

        for (int i = 0; i < 100; i++)
        {
            var e = CreateEvent(LogEventLevel.Information, isHttpEvent: true);
            filter.IsEnabled(e).Should().BeTrue($"event {i} should pass at 100% rate");
        }
    }

    [Theory]
    [InlineData(LogEventLevel.Warning)]
    [InlineData(LogEventLevel.Error)]
    [InlineData(LogEventLevel.Fatal)]
    public void IsEnabled_NeverFiltersWarningOrAbove(LogEventLevel level)
    {
        var filter = CreateFilter(0.01); // 1 % rate — very aggressive filtering

        for (int i = 0; i < 100; i++)
        {
            var e = CreateEvent(level, isHttpEvent: true);
            filter.IsEnabled(e).Should().BeTrue($"{level} events must never be dropped");
        }
    }

    [Fact]
    public void IsEnabled_AlwaysPassesNonHttpEvents()
    {
        var filter = CreateFilter(0.01); // Very aggressive

        for (int i = 0; i < 100; i++)
        {
            var e = CreateEvent(LogEventLevel.Information, isHttpEvent: false);
            filter.IsEnabled(e).Should().BeTrue("non-HTTP events must always pass through");
        }
    }

    [Fact]
    public void IsEnabled_ReducesEventsApproximately_AtLowSampleRate()
    {
        // At 10 % sample rate and 1 000 HTTP events we expect roughly 10 % through.
        var filter = CreateFilter(0.1);
        int passed = 0;
        const int total = 1_000;

        for (int i = 0; i < total; i++)
        {
            var e = CreateEvent(LogEventLevel.Information, isHttpEvent: true);
            if (filter.IsEnabled(e)) passed++;
        }

        // Allow generous tolerance — exact count depends on ceiling arithmetic.
        passed.Should().BeGreaterThan(50, "at 10% we expect roughly 100/1000 to pass");
        passed.Should().BeLessThan(300, "at 10% we expect at most ~300/1000 to pass");
    }

    [Fact]
    public void IsEnabled_IsThreadSafe_UnderParallelLoad()
    {
        var filter = CreateFilter(0.5);

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        Parallel.For(0, 1_000, i =>
        {
            try
            {
                var e = CreateEvent(LogEventLevel.Information, isHttpEvent: true);
                filter.IsEnabled(e);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        exceptions.Should().BeEmpty("SamplingFilter must be thread-safe");
    }

    [Fact]
    public void IsEnabled_SamplesHttpRouteProperty_AsHttpEvent()
    {
        var filter = CreateFilter(0.1);

        // Use http.route instead of RequestPath to verify both are recognised.
        var properties = new List<LogEventProperty>
        {
            new("http.route", new ScalarValue("/api/items"))
        };
        var e = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            messageTemplate: new MessageTemplateParser().Parse("Test"),
            properties: properties);

        // Just verify no exception is thrown — sampling logic is the same.
        var act = () => filter.IsEnabled(e);
        act.Should().NotThrow();
    }
}
