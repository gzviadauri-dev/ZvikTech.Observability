using System;
using System.Collections.Generic;
using System.Threading;
using Company.Observability.Configuration;
using Company.Observability.Logging;
using FluentAssertions;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Company.Observability.Tests;

public sealed class RateLimitedLogFilterTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private static RateLimitedLogFilter CreateFilter(
        bool enabled = true,
        int maxPerWindow = 3,
        int windowSeconds = 60)
    {
        return new RateLimitedLogFilter(new RateLimitOptions
        {
            Enabled = enabled,
            MaxPerWindow = maxPerWindow,
            WindowSeconds = windowSeconds
        });
    }

    private static LogEvent CreateEvent(LogEventLevel level, string template = "Test {X}")
    {
        return new LogEvent(
            DateTimeOffset.UtcNow,
            level,
            exception: null,
            messageTemplate: new MessageTemplateParser().Parse(template),
            properties: new List<LogEventProperty>());
    }

    // ── Tests ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_PassesFirstNEvents_WithinWindow()
    {
        var filter = CreateFilter(maxPerWindow: 3);
        const string template = "Operation {Op} failed";

        for (int i = 0; i < 3; i++)
        {
            filter.IsEnabled(CreateEvent(LogEventLevel.Warning, template))
                .Should().BeTrue($"event {i + 1} of 3 should pass");
        }
    }

    [Fact]
    public void IsEnabled_BlocksEventsExceedingMaxPerWindow()
    {
        var filter = CreateFilter(maxPerWindow: 3);
        const string template = "Repeated error {Code}";

        // Consume all 3 allowed slots.
        for (int i = 0; i < 3; i++)
        {
            filter.IsEnabled(CreateEvent(LogEventLevel.Warning, template));
        }

        // The 4th and beyond should be blocked.
        filter.IsEnabled(CreateEvent(LogEventLevel.Warning, template))
            .Should().BeFalse("4th occurrence exceeds MaxPerWindow=3");

        filter.IsEnabled(CreateEvent(LogEventLevel.Warning, template))
            .Should().BeFalse("5th occurrence exceeds MaxPerWindow=3");
    }

    [Fact]
    public void IsEnabled_NeverBlocksErrorEvents()
    {
        var filter = CreateFilter(maxPerWindow: 1);
        const string template = "Critical failure {Reason}";

        // Exhaust the allowed slot.
        filter.IsEnabled(CreateEvent(LogEventLevel.Error, template));

        // Additional Error events must still pass.
        for (int i = 0; i < 10; i++)
        {
            filter.IsEnabled(CreateEvent(LogEventLevel.Error, template))
                .Should().BeTrue($"Error event {i} must never be rate-limited");
        }
    }

    [Fact]
    public void IsEnabled_NeverBlocksFatalEvents()
    {
        var filter = CreateFilter(maxPerWindow: 1);
        const string template = "Fatal crash {Detail}";

        filter.IsEnabled(CreateEvent(LogEventLevel.Fatal, template));

        for (int i = 0; i < 5; i++)
        {
            filter.IsEnabled(CreateEvent(LogEventLevel.Fatal, template))
                .Should().BeTrue($"Fatal event {i} must never be rate-limited");
        }
    }

    [Fact]
    public void IsEnabled_ResetsAfterWindowExpires()
    {
        // Use 1-second window so we can actually wait for it.
        var filter = CreateFilter(maxPerWindow: 2, windowSeconds: 1);
        const string template = "Transient {Event}";

        // Exhaust the window.
        filter.IsEnabled(CreateEvent(LogEventLevel.Warning, template));
        filter.IsEnabled(CreateEvent(LogEventLevel.Warning, template));
        filter.IsEnabled(CreateEvent(LogEventLevel.Warning, template))
            .Should().BeFalse("3rd event within window is blocked");

        // Wait for the window to expire.
        Thread.Sleep(1_100);

        // After the window resets, events should pass again.
        filter.IsEnabled(CreateEvent(LogEventLevel.Warning, template))
            .Should().BeTrue("first event of new window should pass");
    }

    [Fact]
    public void IsEnabled_DifferentTemplates_HaveIndependentBuckets()
    {
        var filter = CreateFilter(maxPerWindow: 2);

        const string templateA = "Template A {X}";
        const string templateB = "Template B {Y}";

        // Exhaust buckets for A.
        filter.IsEnabled(CreateEvent(LogEventLevel.Warning, templateA));
        filter.IsEnabled(CreateEvent(LogEventLevel.Warning, templateA));

        // Template B should still have its own fresh bucket.
        filter.IsEnabled(CreateEvent(LogEventLevel.Warning, templateB))
            .Should().BeTrue("template B has its own independent rate-limit bucket");

        // Template A is now blocked.
        filter.IsEnabled(CreateEvent(LogEventLevel.Warning, templateA))
            .Should().BeFalse("template A bucket is exhausted");
    }

    [Fact]
    public void IsEnabled_WhenDisabled_PassesAllEvents()
    {
        var filter = CreateFilter(enabled: false, maxPerWindow: 1);
        const string template = "Should always pass {X}";

        for (int i = 0; i < 10; i++)
        {
            filter.IsEnabled(CreateEvent(LogEventLevel.Warning, template))
                .Should().BeTrue("rate limiting is disabled");
        }
    }

    [Fact]
    public void IsEnabled_IsThreadSafe_UnderParallelLoad()
    {
        var filter = CreateFilter(maxPerWindow: 100, windowSeconds: 60);
        const string template = "Concurrent test {Id}";

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        System.Threading.Tasks.Parallel.For(0, 500, i =>
        {
            try
            {
                filter.IsEnabled(CreateEvent(LogEventLevel.Warning, template));
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        exceptions.Should().BeEmpty("RateLimitedLogFilter must be thread-safe");
    }
}
