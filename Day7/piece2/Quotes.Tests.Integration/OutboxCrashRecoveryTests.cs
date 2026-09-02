using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quotes.Tests.Integration.TestDoubles;
using QuotesApi.Data;
using QuotesApi.Messaging;
using QuotesApi.Messaging.Outbox;
using QuotesApi.Models;

namespace Quotes.Tests.Integration;

/// <summary>
/// The claim the task asks to be proved, end to end through the real HTTP
/// pipeline: a crash in the publish step loses nothing.
///
/// The shape of every test here is the same, and it mirrors the manual
/// kill-the-process run in the submission:
///
///   1. POST a quote with no relay running. This is the state a process that
///      died between commit and publish leaves behind -- and it is genuinely
///      that state, not an approximation of it: the row is committed, the
///      message is not sent, and nothing in the process remembers it needs to
///      be. The intent survived the crash because it is a database row.
///   2. Start a relay, as a restarted process would.
///   3. Assert the event is delivered, exactly once, with no intervention.
///
/// The relay is driven with RunOnceAsync rather than started as a hosted
/// service, so nothing here waits on a poll interval.
/// </summary>
public class OutboxCrashRecoveryTests : IAsyncLifetime
{
    private RecordingPublisherFactory _factory = null!;
    private HttpClient _client = null!;
    private string _token = null!;

    public async Task InitializeAsync()
    {
        _factory = new RecordingPublisherFactory();
        _client = _factory.CreateClient();
        _token = await OutboxAtomicityTests.IssueTokenAsync(_factory);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<int> PostQuoteAsync(string author, string text)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/quotes");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Content = JsonContent.Create(new { author, text });

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return int.Parse(response.Headers.Location!.ToString().Split('/').Last());
    }

    /// <summary>
    /// A relay over the running app's own database and DI container -- the
    /// same thing the hosted service would be, only driven a pass at a time.
    /// </summary>
    private OutboxRelayService BuildRelay(OutboxOptions? options = null) =>
        new(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new ChannelOutboxSignal(),
            Options.Create(options ?? new OutboxOptions { BatchSize = 20, MaxAttempts = 3 }),
            new OutboxMetrics(),
            _factory.Clock,
            NullLogger<OutboxRelayService>.Instance);

    [Fact]
    public async Task An_event_committed_before_the_crash_is_published_after_the_restart()
    {
        var quoteId = await PostQuoteAsync("Marcus Aurelius", "The obstacle is the way.");

        // The pre-restart state, asserted rather than assumed. This is the
        // step that makes the recovery below meaningful: without it, a test
        // that ends with "the message arrived" cannot tell recovery apart
        // from a publish that simply happened on time.
        _factory.Publisher.Published.Should().BeEmpty("nothing published: the process 'died' before the relay ran");

        var relay = BuildRelay();
        await relay.RunOnceAsync(CancellationToken.None);

        _factory.Publisher.Published.Should().HaveCount(1);
        _factory.Publisher.Published[0].QuoteId.Should().Be(quoteId);
        _factory.Publisher.Published[0].EventType.Should().Be("QuoteCreated");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
        var row = await db.OutboxMessages.AsNoTracking().SingleAsync();
        row.Status.Should().Be(OutboxStatus.Sent);
    }

    [Fact]
    public async Task A_backlog_that_built_up_while_the_relay_was_down_drains_in_order()
    {
        // Ten writes accepted with no relay running -- a broker outage, or a
        // relay that was rolled and did not come back. Every one of them is a
        // 201 to the caller, and every one of them is still deliverable.
        for (var i = 1; i <= 10; i++)
            await PostQuoteAsync($"Author {i}", $"Quote {i}");

        _factory.Publisher.Published.Should().BeEmpty();

        var relay = BuildRelay(new OutboxOptions { BatchSize = 4, MaxAttempts = 3 });

        await relay.RunOnceAsync(CancellationToken.None);
        _factory.Publisher.Published.Should().HaveCount(4, "one batch per pass");

        await relay.RunOnceAsync(CancellationToken.None);
        await relay.RunOnceAsync(CancellationToken.None);

        var published = _factory.Publisher.Published;
        published.Should().HaveCount(10);
        published.Select(e => e.QuoteId).Should().BeInAscendingOrder("claimed in Id order");
        published.Select(e => e.EventId).Should().OnlyHaveUniqueItems();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
        (await db.OutboxMessages.CountAsync(m => m.Status == OutboxStatus.Pending)).Should().Be(0);
    }

    [Fact]
    public async Task Restarting_the_relay_does_not_republish_what_the_previous_one_sent()
    {
        await PostQuoteAsync("Seneca", "Luck is what happens when preparation meets opportunity.");

        var before = BuildRelay();
        await before.RunOnceAsync(CancellationToken.None);

        // A second relay with a different LockOwner -- a restart, or the next
        // instance in a rolling deploy. The Sent status, not any in-memory
        // bookkeeping, is what stops it re-sending.
        var after = BuildRelay();
        await after.RunOnceAsync(CancellationToken.None);
        await after.RunOnceAsync(CancellationToken.None);

        _factory.Publisher.Published.Should().HaveCount(1);
    }

    /// <summary>
    /// The real app with a recording publisher in place of the no-op one, so a
    /// test can see what the relay actually sent.
    /// </summary>
    private sealed class RecordingPublisherFactory : QuotesApiFactory
    {
        public RecordingOutboxPublisher Publisher { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IQuoteEventPublisher>();
                services.AddSingleton<IQuoteEventPublisher>(Publisher);
            });
        }
    }
}

/// <summary>Records what the relay published. Integration-side twin of the unit test double.</summary>
public sealed class RecordingOutboxPublisher : IQuoteEventPublisher
{
    private readonly List<QuoteChangedEvent> _published = new();

    public IReadOnlyList<QuoteChangedEvent> Published
    {
        get { lock (_published) return _published.ToList(); }
    }

    public Task PublishAsync(QuoteChangedEvent evt, CancellationToken cancellationToken = default)
    {
        lock (_published) _published.Add(evt);
        return Task.CompletedTask;
    }
}
