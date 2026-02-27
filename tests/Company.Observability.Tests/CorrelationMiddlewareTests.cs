using System;
using System.Reflection;
using System.Threading.Tasks;
using Company.Observability.Configuration;
using Company.Observability.Logging;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Xunit;

namespace Company.Observability.Tests;

public sealed class CorrelationMiddlewareTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private static CorrelationMiddleware CreateMiddleware(string headerName = "X-Correlation-Id")
    {
        var options = new ObservabilityOptions
        {
            ServiceName = "test",
            Logging = new LoggingOptions { CorrelationHeaderName = headerName }
        };
        return new CorrelationMiddleware(Options.Create(options));
    }

    private static DefaultHttpContext CreateContext(string? correlationHeaderValue = null,
        string headerName = "X-Correlation-Id",
        bool eagerOnStarting = false)
    {
        DefaultHttpContext context;

        if (eagerOnStarting)
        {
            // Use an IHttpResponseFeature that fires OnStarting callbacks immediately.
            // This makes unit tests work without a full HTTP pipeline.
            var features = new FeatureCollection();
            features.Set<IHttpResponseFeature>(new EagerOnStartingResponseFeature());
            features.Set<IHttpRequestFeature>(new HttpRequestFeature());
            context = new DefaultHttpContext(features);
        }
        else
        {
            context = new DefaultHttpContext();
        }

        if (correlationHeaderValue is not null)
        {
            context.Request.Headers[headerName] = correlationHeaderValue;
        }

        return context;
    }

    /// <summary>
    /// An <see cref="IHttpResponseFeature"/> that fires <c>OnStarting</c> callbacks
    /// immediately when registered, enabling response-header assertions in unit tests
    /// where there is no real ASP.NET Core HTTP pipeline to call <c>StartAsync</c>.
    /// </summary>
    private sealed class EagerOnStartingResponseFeature : HttpResponseFeature
    {
        public override void OnStarting(Func<object, Task> callback, object state)
        {
            // Execute synchronously — acceptable in unit tests; production code always
            // goes through the real pipeline where callbacks fire async before headers flush.
            callback(state).GetAwaiter().GetResult();
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_ReadsCorrelationIdFromHeader_WhenPresent()
    {
        const string expectedId = "my-correlation-123";
        var middleware = CreateMiddleware();
        var context = CreateContext(expectedId);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        // Verify the value was stored in CorrelationContext during middleware execution.
        // (After execution the async-local is cleared, so we capture inside the next delegate.)
        string? capturedId = null;
        await middleware.InvokeAsync(CreateContext(expectedId),
            ctx =>
            {
                capturedId = CorrelationContext.CorrelationId;
                return Task.CompletedTask;
            });

        capturedId.Should().Be(expectedId);
    }

    [Fact]
    public async Task InvokeAsync_GeneratesNewGuid_WhenHeaderIsMissing()
    {
        var middleware = CreateMiddleware();
        var context = CreateContext(); // No header

        string? capturedId = null;
        await middleware.InvokeAsync(context, ctx =>
        {
            capturedId = CorrelationContext.CorrelationId;
            return Task.CompletedTask;
        });

        capturedId.Should().NotBeNullOrEmpty();
        System.Guid.TryParse(capturedId, out _).Should().BeTrue("generated ID must be a valid GUID");
    }

    [Fact]
    public async Task InvokeAsync_GeneratesNewGuid_WhenHeaderIsWhitespace()
    {
        var middleware = CreateMiddleware();
        var context = CreateContext("   "); // Whitespace only

        string? capturedId = null;
        await middleware.InvokeAsync(context, ctx =>
        {
            capturedId = CorrelationContext.CorrelationId;
            return Task.CompletedTask;
        });

        capturedId.Should().NotBeNullOrEmpty();
        capturedId!.Trim().Should().NotBeEmpty();
        capturedId.Should().NotBe("   ");
    }

    [Fact]
    public async Task InvokeAsync_SetsCorrelationIdOnResponseHeader()
    {
        const string inboundId = "inbound-correlation-id";
        var middleware = CreateMiddleware();
        // eagerOnStarting=true installs EagerOnStartingResponseFeature which fires
        // OnStarting callbacks immediately — no real HTTP pipeline required.
        var context = CreateContext(inboundId, eagerOnStarting: true);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        context.Response.Headers["X-Correlation-Id"].ToString().Should().Be(inboundId);
    }

    [Fact]
    public async Task InvokeAsync_SetsGeneratedIdOnResponseHeader_WhenNoInboundHeader()
    {
        var middleware = CreateMiddleware();
        var context = CreateContext(eagerOnStarting: true);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        context.Response.Headers["X-Correlation-Id"].ToString()
            .Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task InvokeAsync_StoresCorrelationIdInCorrelationContext()
    {
        const string expectedId = "ctx-test-id";
        var middleware = CreateMiddleware();
        var context = CreateContext(expectedId);

        string? storedId = null;
        await middleware.InvokeAsync(context, ctx =>
        {
            storedId = CorrelationContext.CorrelationId;
            return Task.CompletedTask;
        });

        storedId.Should().Be(expectedId);
    }

    [Fact]
    public async Task InvokeAsync_ClearsCorrelationContext_AfterRequestCompletes()
    {
        var middleware = CreateMiddleware();
        var context = CreateContext("id-to-clear");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        // After the middleware completes, the AsyncLocal should be cleared.
        CorrelationContext.CorrelationId.Should().BeNull();
        CorrelationContext.RequestId.Should().BeNull();
    }

    [Fact]
    public async Task InvokeAsync_UsesCustomHeaderName_FromOptions()
    {
        const string customHeader = "X-Request-ID";
        const string expectedId = "custom-header-id";
        var middleware = CreateMiddleware(customHeader);
        var context = CreateContext(expectedId, customHeader);

        string? capturedId = null;
        await middleware.InvokeAsync(context, ctx =>
        {
            capturedId = CorrelationContext.CorrelationId;
            return Task.CompletedTask;
        });

        capturedId.Should().Be(expectedId);
    }
}
