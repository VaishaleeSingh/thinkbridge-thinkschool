using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quotes.Tests.Integration.TestDoubles;
using QuotesApi.Data;
using QuotesApi.Messaging;
using QuotesApi.Messaging.Outbox;
using QuotesApi.Models;
using QuotesApi.Services;

namespace Quotes.Tests.Integration;

/// <summary>
/// The transaction, through the real HTTP pipeline.
///
/// These run with Outbox:RelayEnabled at its default of false, and that is
/// what makes them possible: with no relay draining the table, a test can
/// assert that a committed write left a Pending row behind and that nothing
/// consumed it. Wire the relay in here and every assertion below becomes a
/// race against a background service.
/// </summary>
public class OutboxAtomicityTests : IAsyncLifetime
{
    private QuotesApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _token = null!;

    public async Task InitializeAsync()
    {
        _factory = new QuotesApiFactory();
        _client = _factory.CreateClient();
        _token = await IssueTokenAsync(_factory);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    internal static async Task<string> IssueTokenAsync(QuotesApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var user = new User
        {
            Email = $"outbox-{Guid.NewGuid():N}@example.com",
            PasswordHash = "unused",
            CreatedAt = factory.Clock.UtcNow.UtcDateTime
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return authService.GenerateAccessToken(user);
    }

    private HttpRequestMessage Authed(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private async Task<List<OutboxMessage>> ReadOutboxAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();

        return await db.OutboxMessages.AsNoTracking().OrderBy(m => m.Id).ToListAsync();
    }

    [Fact]
    public async Task Creating_a_quote_commits_the_quote_and_its_event_together()
    {
        var request = Authed(HttpMethod.Post, "/api/quotes");
        request.Content = JsonContent.Create(new
        {
            author = "Seneca",
            text = "We suffer more in imagination than in reality."
        });

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var rows = await ReadOutboxAsync();
        rows.Should().HaveCount(1);

        var row = rows[0];
        row.EventType.Should().Be("QuoteCreated");
        row.Status.Should().Be(OutboxStatus.Pending, "nothing has published it: the relay is off");
        row.Attempts.Should().Be(0);
        row.SentAtUtc.Should().BeNull();
        row.Payload.Should().Contain("Seneca");

        // The MessageId is the deterministic EventId, which is what lets a
        // consumer recognise a redelivery of this exact event after any
        // restart on either side.
        var quoteId = int.Parse(response.Headers.Location!.ToString().Split('/').Last());
        row.MessageId.Should().Be(
            QuoteChangedEvent.BuildEventId("QuoteCreated", quoteId, _factory.Clock.UtcNow));
    }

    [Fact]
    public async Task Updating_and_deleting_each_enqueue_their_own_event()
    {
        var create = Authed(HttpMethod.Post, "/api/quotes");
        create.Content = JsonContent.Create(new { author = "Epictetus", text = "First draft." });
        var created = await _client.SendAsync(create);
        var location = created.Headers.Location!.ToString();

        var update = Authed(HttpMethod.Put, location);
        update.Content = JsonContent.Create(new { author = "Epictetus", text = "Second draft." });
        (await _client.SendAsync(update)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _client.SendAsync(Authed(HttpMethod.Delete, location)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rows = await ReadOutboxAsync();
        rows.Select(r => r.EventType).Should().Equal("QuoteCreated", "QuoteUpdated", "QuoteDeleted");
        rows.Should().OnlyContain(r => r.Status == OutboxStatus.Pending);

        // Ordering is by Id, the insertion sequence, and not by OccurredAtUtc
        // -- which under a frozen test clock is identical across all three.
        // That is exactly why Id is the sequencer.
        rows.Select(r => r.Id).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task A_failed_enqueue_takes_the_quote_down_with_it()
    {
        // The direction of atomicity that is usually left untested. If the
        // quote survived a failed enqueue, the transaction would be
        // decorative: the API would be right back to committing changes whose
        // events nobody will ever send.
        await using var factory = new OutboxFailureFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(factory);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/quotes");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { author = "Nobody", text = "This must not survive." });

        var response = await client.SendAsync(request);

        // The status code is not the property under test -- the ROLLBACK is.
        // Which code a failed enqueue produces is ExceptionHandlingMiddleware's
        // decision, and pinning an exact one here would make this test fail the
        // next time that mapping is tuned, for a reason that has nothing to do
        // with atomicity.
        response.IsSuccessStatusCode.Should().BeFalse();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();

        (await db.Quotes.CountAsync(q => q.Author == "Nobody"))
            .Should().Be(0, "the transaction rolled back, so the quote never existed");
        (await db.OutboxMessages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task No_request_path_publishes_to_the_broker()
    {
        // Wires a publisher that throws if anyone calls it, then exercises all
        // three writes. Before Day 20 every one of them would fail this.
        await using var factory = new NoPublishOnRequestPathFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(factory);

        HttpRequestMessage Request(HttpMethod method, string url)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }

        var create = Request(HttpMethod.Post, "/api/quotes");
        create.Content = JsonContent.Create(new { author = "Zeno", text = "Well-being is realised by small steps." });
        var created = await client.SendAsync(create);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var location = created.Headers.Location!.ToString();

        var update = Request(HttpMethod.Put, location);
        update.Content = JsonContent.Create(new { author = "Zeno", text = "But they are no small things." });
        (await client.SendAsync(update)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.SendAsync(Request(HttpMethod.Delete, location)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
        (await db.OutboxMessages.CountAsync()).Should().Be(3, "all three events are durable, none was published");
    }

    /// <summary>The real app, with an outbox writer that cannot stage a row.</summary>
    private sealed class OutboxFailureFactory : QuotesApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IOutboxWriter>();
                services.AddScoped<IOutboxWriter, ThrowingOutboxWriter>();
            });
        }
    }

    /// <summary>The real app, with a publisher that treats any call as a bug.</summary>
    private sealed class NoPublishOnRequestPathFactory : QuotesApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IQuoteEventPublisher>();
                services.AddSingleton<IQuoteEventPublisher, ExplodingQuoteEventPublisher>();
            });
        }
    }
}
