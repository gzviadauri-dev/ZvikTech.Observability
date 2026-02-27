using System;
using System.Collections.Generic;
using System.Linq;
using Company.Observability.Configuration;
using Company.Observability.Logging;
using FluentAssertions;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Company.Observability.Tests;

public sealed class RedactionEnricherTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private static RedactionEnricher CreateEnricher(List<string>? customKeys = null)
    {
        var options = new ObservabilityOptions
        {
            ServiceName = "test",
            Redaction = new RedactionOptions
            {
                SensitiveKeys = customKeys ?? new List<string>()
            }
        };
        return new RedactionEnricher(options);
    }

    private static LogEvent CreateEventWithProperty(string propertyName, object? value)
    {
        var properties = new List<LogEventProperty>
        {
            new(propertyName, new ScalarValue(value))
        };
        return new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            messageTemplate: new MessageTemplateParser().Parse("Test"),
            properties: properties);
    }

    private static string? GetPropertyValue(LogEvent logEvent, string propertyName)
    {
        if (logEvent.Properties.TryGetValue(propertyName, out var value))
        {
            return value is ScalarValue sv ? sv.Value?.ToString() : value.ToString();
        }
        return null;
    }

    // ── Tests: default sensitive keys ────────────────────────────────────────────────────

    [Theory]
    [InlineData("password")]
    [InlineData("passwd")]
    [InlineData("secret")]
    [InlineData("token")]
    [InlineData("apikey")]
    [InlineData("api_key")]
    [InlineData("authorization")]
    [InlineData("auth")]
    [InlineData("credential")]
    [InlineData("credentials")]
    [InlineData("connectionstring")]
    [InlineData("connection_string")]
    [InlineData("privatekey")]
    [InlineData("private_key")]
    [InlineData("clientsecret")]
    [InlineData("client_secret")]
    [InlineData("personalid")]
    [InlineData("ssn")]
    [InlineData("cardnumber")]
    [InlineData("card_number")]
    [InlineData("cvv")]
    [InlineData("pin")]
    public void Enrich_RedactsAllDefaultSensitiveKeys(string sensitiveKey)
    {
        var enricher = CreateEnricher();
        var logEvent = CreateEventWithProperty(sensitiveKey, "super-secret-value");

        enricher.Enrich(logEvent, new TestPropertyFactory());

        GetPropertyValue(logEvent, sensitiveKey).Should().Be("***");
    }

    [Fact]
    public void Enrich_DoesNotRedactNonSensitiveProperty()
    {
        var enricher = CreateEnricher();
        var logEvent = CreateEventWithProperty("userId", "user-123");

        enricher.Enrich(logEvent, new TestPropertyFactory());

        GetPropertyValue(logEvent, "userId").Should().Be("user-123");
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("PASSWORD")]
    [InlineData("ApiKey")]
    [InlineData("APIKEY")]
    [InlineData("Authorization")]
    public void Enrich_IsCaseInsensitiveForDefaultKeys(string sensitiveKey)
    {
        var enricher = CreateEnricher();
        var logEvent = CreateEventWithProperty(sensitiveKey, "should-be-redacted");

        enricher.Enrich(logEvent, new TestPropertyFactory());

        GetPropertyValue(logEvent, sensitiveKey).Should().Be("***");
    }

    [Fact]
    public void Enrich_RedactsCustomKeyFromOptions()
    {
        var enricher = CreateEnricher(new List<string> { "nationalId", "taxNumber" });

        var logEvent = CreateEventWithProperty("nationalId", "123-45-6789");
        enricher.Enrich(logEvent, new TestPropertyFactory());

        GetPropertyValue(logEvent, "nationalId").Should().Be("***");
    }

    [Fact]
    public void Enrich_CustomKeysAreCaseInsensitive()
    {
        var enricher = CreateEnricher(new List<string> { "NationalId" });

        var logEvent = CreateEventWithProperty("nationalid", "sensitive");
        enricher.Enrich(logEvent, new TestPropertyFactory());

        GetPropertyValue(logEvent, "nationalid").Should().Be("***");
    }

    [Fact]
    public void Enrich_DefaultKeysMergedWithCustomKeys_BothRedacted()
    {
        var enricher = CreateEnricher(new List<string> { "myCustomSecret" });

        var eventWithDefault = CreateEventWithProperty("password", "pass");
        var eventWithCustom = CreateEventWithProperty("myCustomSecret", "secret");

        enricher.Enrich(eventWithDefault, new TestPropertyFactory());
        enricher.Enrich(eventWithCustom, new TestPropertyFactory());

        GetPropertyValue(eventWithDefault, "password").Should().Be("***");
        GetPropertyValue(eventWithCustom, "myCustomSecret").Should().Be("***");
    }

    [Fact]
    public void Enrich_NullPropertyValue_DoesNotThrow()
    {
        var enricher = CreateEnricher();
        var logEvent = CreateEventWithProperty("password", null);

        var act = () => enricher.Enrich(logEvent, new TestPropertyFactory());

        act.Should().NotThrow();
        GetPropertyValue(logEvent, "password").Should().Be("***");
    }

    [Fact]
    public void Enrich_RedactsHttpRequestAuthorizationHeader()
    {
        var enricher = CreateEnricher();
        var logEvent = CreateEventWithProperty("http.request.headers.authorization", "Bearer token123");

        enricher.Enrich(logEvent, new TestPropertyFactory());

        GetPropertyValue(logEvent, "http.request.headers.authorization").Should().Be("***");
    }

    [Fact]
    public void Enrich_DoesNotRedactNonSensitiveHttpHeader()
    {
        var enricher = CreateEnricher();
        var logEvent = CreateEventWithProperty("http.request.headers.content-type", "application/json");

        enricher.Enrich(logEvent, new TestPropertyFactory());

        GetPropertyValue(logEvent, "http.request.headers.content-type")
            .Should().Be("application/json");
    }

    // ── Test helper ───────────────────────────────────────────────────────────────────────

    private sealed class TestPropertyFactory : Serilog.Core.ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }
}
