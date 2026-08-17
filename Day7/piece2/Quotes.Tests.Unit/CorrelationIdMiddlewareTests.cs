using System.Diagnostics;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using QuotesApi.Middleware;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Quotes.Tests.Unit;

/// <summary>
/// Verifies the correlation-ID behaviour that all the structured logging in
/// this app depends on: that every log line written while a request is being
/// handled carries that request's TraceId, and -- just as important -- that
/// the TraceId does NOT survive past the end of the request.
///
/// These run against a real Serilog logger (real LogContext enricher, real
/// pipeline), just with a tiny local sink standing in for the console so the
/// emitted LogEvents can be read back and asserted on. Console output cannot
/// be asserted on, and "it looked right when I ran it" would not catch the
/// regressions that actually matter here.
/// </summary>
public class CorrelationIdMiddlewareTests
{
    /// <summary>
    /// Captures LogEvents in memory so a test can inspect the properties
    /// Serilog actually attached. Deliberately a few lines of local code
    /// rather than a third-party in-memory sink package: this needs no
    /// shared static singleton to reset between tests, so two tests can
    /// never contaminate each other through it.
    /// </summary>
    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (ILogger logger, CollectingSink sink) CreateLogger()
    {
        var sink = new CollectingSink();

        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        return (logger, sink);
    }

    private static string? TraceIdOf(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("TraceId", out var value) && value is ScalarValue scalar
            ? scalar.Value as string
            : null;

    [Fact]
    public async Task Invoke_StampsTheRequestsTraceIdOnLogLinesWrittenDownstream()
    {
        // Arrange
        var (logger, sink) = CreateLogger();
        var context = new DefaultHttpContext { TraceIdentifier = "trace-id-under-test" };

        // "next" stands in for everything further down the pipeline -- an
        // endpoint, a service, a repository. It should not have to know
        // anything about correlation IDs for its logs to carry one.
        var middleware = new CorrelationIdMiddleware(_ =>
        {
            logger.Information("Created quote {QuoteId}", 42);
            return Task.CompletedTask;
        });

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        sink.Events.Should().ContainSingle();
        TraceIdOf(sink.Events[0]).Should().Be("trace-id-under-test");
    }

    [Fact]
    public async Task Invoke_DoesNotLeaveTheTraceIdAttachedAfterTheRequestEnds()
    {
        // Arrange
        var (logger, sink) = CreateLogger();
        var context = new DefaultHttpContext { TraceIdentifier = "trace-id-under-test" };

        var middleware = new CorrelationIdMiddleware(_ =>
        {
            logger.Information("during the request");
            return Task.CompletedTask;
        });

        // Act
        await middleware.InvokeAsync(context);
        logger.Information("after the request has finished");

        // Assert -- if the pushed property were never popped (e.g. someone
        // drops the "using" and just calls PushProperty), this second line
        // would wrongly inherit the finished request's TraceId, quietly
        // mis-attributing later work to an unrelated request.
        sink.Events.Should().HaveCount(2);
        TraceIdOf(sink.Events[0]).Should().Be("trace-id-under-test");
        TraceIdOf(sink.Events[1]).Should().BeNull();
    }

    [Fact]
    public async Task Invoke_GivesTwoDifferentRequestsDifferentTraceIds()
    {
        // Arrange
        var (logger, sink) = CreateLogger();

        async Task HandleRequestAsync(string traceIdentifier)
        {
            var context = new DefaultHttpContext { TraceIdentifier = traceIdentifier };
            var middleware = new CorrelationIdMiddleware(_ =>
            {
                logger.Information("handling {TraceIdentifier}", traceIdentifier);
                return Task.CompletedTask;
            });

            await middleware.InvokeAsync(context);
        }

        // Act
        await HandleRequestAsync("first-request");
        await HandleRequestAsync("second-request");

        // Assert -- the whole reason a correlation ID is useful is that it
        // separates one request's lines from another's.
        sink.Events.Should().HaveCount(2);
        TraceIdOf(sink.Events[0]).Should().Be("first-request");
        TraceIdOf(sink.Events[1]).Should().Be("second-request");
    }

    [Fact]
    public async Task Invoke_WhenAnActivityIsRunning_UsesTheActivitysTraceIdNotTheHttpContextOne()
    {
        // Arrange -- an ambient Activity is what OpenTelemetry's
        // instrumentation creates for every incoming request, and its
        // TraceId is the one the trace backend indexes the trace under.
        var (logger, sink) = CreateLogger();

        var context = new DefaultHttpContext
        {
            TraceIdentifier = "aspnet-core-trace-identifier"
        };

        var middleware = new CorrelationIdMiddleware(_ =>
        {
            logger.Information("inside the request");
            return Task.CompletedTask;
        });

        using var activity = new Activity("incoming-request").Start();

        // Act
        await middleware.InvokeAsync(context);

        // Assert -- the logged TraceId must be the Activity's, because that
        // is the value that will let someone jump from this log line to the
        // matching trace. If it fell back to TraceIdentifier here, logs and
        // traces would each be internally consistent and mutually useless.
        sink.Events.Should().ContainSingle();
        TraceIdOf(sink.Events[0]).Should().Be(activity.TraceId.ToString());
        TraceIdOf(sink.Events[0]).Should().NotBe("aspnet-core-trace-identifier");
    }

    [Fact]
    public async Task Invoke_WhenNoActivityIsRunning_FallsBackToTheHttpContextTraceIdentifier()
    {
        // Arrange -- tracing disabled, or code running outside a request.
        // There is still a request to correlate, so something is better
        // than an empty property.
        var (logger, sink) = CreateLogger();

        var context = new DefaultHttpContext
        {
            TraceIdentifier = "aspnet-core-trace-identifier"
        };

        var middleware = new CorrelationIdMiddleware(_ =>
        {
            logger.Information("inside the request");
            return Task.CompletedTask;
        });

        // Guard: make sure nothing left an Activity running from an earlier
        // test on this thread, which would silently invalidate this test.
        Activity.Current.Should().BeNull();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        sink.Events.Should().ContainSingle();
        TraceIdOf(sink.Events[0]).Should().Be("aspnet-core-trace-identifier");
    }
}
