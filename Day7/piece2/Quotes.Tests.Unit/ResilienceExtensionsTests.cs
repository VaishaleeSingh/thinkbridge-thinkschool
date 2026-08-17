using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuotesApi.Extensions;

namespace Quotes.Tests.Unit;

/// <summary>
/// Tests for the Polly pipeline wrapped around the Entra ID metadata
/// client.
///
/// These assert the two properties that are worth protecting, and skip the
/// ones that are not worth the test time. Worth protecting: a transient
/// failure is retried rather than surfaced, every retry is logged, and a
/// client error is NOT retried. Not tested here: the circuit breaker
/// opening, which needs at least MinimumThroughput (10) failing calls
/// inside a 30 second window and would trade seconds of test runtime for a
/// property that is a configured constant rather than logic; and the total
/// timeout, which would mean sleeping past ten seconds.
///
/// The primary handler is replaced with a stub, so nothing here touches
/// the network -- the retry behaviour under test lives entirely in the
/// pipeline that sits above it.
/// </summary>
public class ResilienceExtensionsTests
{
    [Fact]
    public async Task EntraIdClient_WhenTransientFailure_RetriesAndSucceeds()
    {
        var handler = new SequencedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var provider = BuildProvider(handler, out _);

        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ResilienceExtensions.EntraIdClientName);

        var response = await client.GetAsync("https://login.microsoftonline.test/common/v2.0/.well-known/openid-configuration");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Two calls reached the handler: the original 503 and the retry.
        // Asserting the count, not just the final status, is what
        // distinguishes "the retry worked" from "the first call happened
        // to succeed".
        handler.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task EntraIdClient_WhenRetrying_LogsAWarningForEveryAttempt()
    {
        var handler = new SequencedHandler(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);

        using var provider = BuildProvider(handler, out var logs);

        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ResilienceExtensions.EntraIdClientName);

        await client.GetAsync("https://login.microsoftonline.test/common/v2.0/.well-known/openid-configuration");

        // Two failures means two retries, and the requirement is that no
        // retry is silent: a dependency that needs three attempts must
        // leave a trace, otherwise the only symptom is unexplained
        // latency on a green dashboard.
        var retryWarnings = logs.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("retrying"))
            .ToList();

        retryWarnings.Should().HaveCount(2);
        retryWarnings.Should().OnlyContain(e => e.Message.Contains("ServiceUnavailable"));
    }

    [Fact]
    public async Task EntraIdClient_WhenClientError_DoesNotRetry()
    {
        var handler = new SequencedHandler(HttpStatusCode.NotFound, HttpStatusCode.OK);
        using var provider = BuildProvider(handler, out var logs);

        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ResilienceExtensions.EntraIdClientName);

        var response = await client.GetAsync("https://login.microsoftonline.test/common/v2.0/.well-known/openid-configuration");

        // A 404 is an answer, not a blip. Retrying it is three more ways
        // to be told the same thing, and it delays the real error.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        handler.Attempts.Should().Be(1);
        logs.Entries.Should().NotContain(e => e.Message.Contains("retrying"));
    }

    private static ServiceProvider BuildProvider(HttpMessageHandler handler, out RecordingLoggerProvider logs)
    {
        logs = new RecordingLoggerProvider();
        var captured = logs;

        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(captured);
        });

        services.AddResilientHttpClients();

        // Calling AddHttpClient again with the same name adds to the
        // existing registration rather than replacing it, so the
        // resilience handler configured in AddResilientHttpClients stays
        // in place and only the innermost handler is swapped.
        services
            .AddHttpClient(ResilienceExtensions.EntraIdClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Returns the given status codes in order, one per call, repeating the
    /// last one forever after that. Counts how many times it was called.
    /// </summary>
    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode[] _statusCodes;
        private int _attempts;

        public SequencedHandler(params HttpStatusCode[] statusCodes) => _statusCodes = statusCodes;

        public int Attempts => Volatile.Read(ref _attempts);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _attempts) - 1;
            var status = _statusCodes[Math.Min(index, _statusCodes.Length - 1)];

            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = new();
        private readonly object _gate = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_gate) { return _entries.ToList(); } }
        }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void Dispose() { }

        private void Add(LogEntry entry)
        {
            lock (_gate) { _entries.Add(entry); }
        }

        private sealed class RecordingLogger(RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => owner.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }
}
