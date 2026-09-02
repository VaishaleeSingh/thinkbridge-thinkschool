using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Quotes.Tests.Unit.TestDoubles;
using QuotesApi.Messaging.Outbox;
using QuotesApi.Models;

namespace Quotes.Tests.Unit.Messaging.Outbox;

/// <summary>
/// The durability tests. Between them these are the answer to "prove no
/// message is lost if the publish step crashes", at the level a unit test can
/// prove it: every crash point leaves the row in a state from which the
/// message is still delivered.
///
/// Each test drives ONE pass with RunOnceAsync rather than starting the
/// BackgroundService and waiting. A flaky test guarding a durability property
/// is worse than no test, and a poll interval in an assertion is a race.
/// </summary>
public class OutboxRelayServiceTests
{
    [Fact]
    public async Task Publishes_pending_row_and_marks_it_sent()
    {
        await using var host = await OutboxTestHost.CreateAsync();
        var id = await host.EnqueueAsync(quoteId: 1);

        var publisher = new RecordingQuoteEventPublisher();
        var relay = host.BuildRelay(publisher);

        var dispatched = await relay.RunOnceAsync(CancellationToken.None);

        dispatched.Should().Be(1);
        publisher.Published.Should().HaveCount(1);
        publisher.Published[0].QuoteId.Should().Be(1);

        var row = await host.GetRowAsync(id);
        row.Status.Should().Be(OutboxStatus.Sent);
        row.SentAtUtc.Should().NotBeNull();
        row.LockOwner.Should().BeNull("the lease is released once the row is done");
        row.LockedUntilUtc.Should().BeNull();
        row.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Sent_row_is_never_published_twice()
    {
        await using var host = await OutboxTestHost.CreateAsync();
        await host.EnqueueAsync(quoteId: 1);

        var publisher = new RecordingQuoteEventPublisher();
        var relay = host.BuildRelay(publisher);

        await relay.RunOnceAsync(CancellationToken.None);
        await relay.RunOnceAsync(CancellationToken.None);
        await relay.RunOnceAsync(CancellationToken.None);

        publisher.Published.Should().HaveCount(1);
    }

    // ------------------------------------------------------------------
    // Crash point 1: the publish itself fails.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Publish_failure_leaves_the_row_pending_and_costs_one_attempt()
    {
        await using var host = await OutboxTestHost.CreateAsync();
        var id = await host.EnqueueAsync(quoteId: 7);

        var relay = host.BuildRelay(new FlakyQuoteEventPublisher(failuresBeforeSuccess: 99));

        await relay.RunOnceAsync(CancellationToken.None);

        var row = await host.GetRowAsync(id);
        row.Status.Should().Be(OutboxStatus.Pending, "the message is not sent, so it is not done");
        row.Attempts.Should().Be(1);
        row.LastError.Should().Contain("TimeoutException");
        row.LockedUntilUtc.Should().BeNull("the lease is released so the next pass retries at once");
    }

    [Fact]
    public async Task A_transient_outage_delays_the_message_but_never_loses_it()
    {
        await using var host = await OutboxTestHost.CreateAsync();
        var id = await host.EnqueueAsync(quoteId: 7);

        var publisher = new FlakyQuoteEventPublisher(failuresBeforeSuccess: 2);
        var relay = host.BuildRelay(publisher);

        await relay.RunOnceAsync(CancellationToken.None);  // throws
        await relay.RunOnceAsync(CancellationToken.None);  // throws
        await relay.RunOnceAsync(CancellationToken.None);  // succeeds

        publisher.Calls.Should().Be(3);
        publisher.Published.Should().HaveCount(1, "delivered once, after the outage cleared");

        var row = await host.GetRowAsync(id);
        row.Status.Should().Be(OutboxStatus.Sent);
        row.Attempts.Should().Be(3);
    }

    // ------------------------------------------------------------------
    // Crash point 2: the message reached the broker, the row was never
    // marked. This is the one that produces a duplicate, and the one whose
    // behaviour is most worth pinning down.
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_crash_after_the_send_republishes_rather_than_losing_the_message()
    {
        await using var host = await OutboxTestHost.CreateAsync();
        var id = await host.EnqueueAsync(quoteId: 42);

        var publisher = new SendThenCrashQuoteEventPublisher();
        var relay = host.BuildRelay(publisher);

        // Pass 1: the broker has the message; the process dies before the row
        // can be marked.
        await relay.RunOnceAsync(CancellationToken.None);

        var afterCrash = await host.GetRowAsync(id);
        afterCrash.Status.Should().Be(OutboxStatus.Pending);
        publisher.Sent.Should().HaveCount(1);

        // Pass 2: the restarted relay finds the row still pending and sends
        // it again. THIS IS CORRECT. Losing a message cannot be undone; a
        // duplicate is a row the consumer already has, and the consumer's
        // (MessageId, SubscriptionName) primary key rejects the second copy.
        publisher.CrashAfterSend = false;
        await relay.RunOnceAsync(CancellationToken.None);

        publisher.Sent.Should().HaveCount(2, "at-least-once: the duplicate is the price of never losing one");
        publisher.Sent[0].EventId.Should().Be(
            publisher.Sent[1].EventId,
            "the MessageId is deterministic, so the consumer can recognise the duplicate at all");

        var afterRecovery = await host.GetRowAsync(id);
        afterRecovery.Status.Should().Be(OutboxStatus.Sent);
    }

    // ------------------------------------------------------------------
    // Crash point 3: the relay dies holding a claim.
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_row_claimed_by_a_live_relay_is_not_taken_by_another()
    {
        await using var host = await OutboxTestHost.CreateAsync();
        var id = await host.EnqueueAsync(quoteId: 1);

        // Simulate a relay that claimed the row two seconds ago and is still
        // inside its publish.
        await using (var db = host.NewContext())
        {
            await db.OutboxMessages
                .Where(m => m.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(m => m.LockedUntilUtc, host.Clock.UtcNow.UtcDateTime.AddMinutes(1))
                    .SetProperty(m => m.LockOwner, "another-relay"));
        }

        var publisher = new RecordingQuoteEventPublisher();
        var relay = host.BuildRelay(publisher);

        var dispatched = await relay.RunOnceAsync(CancellationToken.None);

        dispatched.Should().Be(0);
        publisher.Published.Should().BeEmpty("two relays publishing the same row is a duplicate with no upside");
    }

    [Fact]
    public async Task An_expired_lease_is_reclaimed_so_a_killed_relay_blocks_nothing()
    {
        await using var host = await OutboxTestHost.CreateAsync();
        var id = await host.EnqueueAsync(quoteId: 1);

        // A relay that was killed mid-batch: its lease is in the table and
        // its process is gone. Nothing will ever release this by hand.
        await using (var db = host.NewContext())
        {
            await db.OutboxMessages
                .Where(m => m.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(m => m.LockedUntilUtc, host.Clock.UtcNow.UtcDateTime.AddMinutes(-1))
                    .SetProperty(m => m.LockOwner, "killed-relay"));
        }

        var publisher = new RecordingQuoteEventPublisher();
        var relay = host.BuildRelay(publisher);

        await relay.RunOnceAsync(CancellationToken.None);

        publisher.Published.Should().HaveCount(1, "lease expiry is why a lease beats a boolean flag");
        (await host.GetRowAsync(id)).Status.Should().Be(OutboxStatus.Sent);
    }

    // ------------------------------------------------------------------
    // Parking: the failure that must not be retried forever.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Poison_is_parked_on_the_first_attempt_not_after_the_whole_budget()
    {
        await using var host = await OutboxTestHost.CreateAsync();
        var id = await host.EnqueueAsync(quoteId: 3);

        var publisher = new PoisonQuoteEventPublisher();
        var relay = host.BuildRelay(publisher, new OutboxOptions { BatchSize = 10, MaxAttempts = 5 });

        await relay.RunOnceAsync(CancellationToken.None);

        publisher.Calls.Should().Be(1, "retrying something that can never succeed spends the batch on it");

        var row = await host.GetRowAsync(id);
        row.Status.Should().Be(OutboxStatus.Failed);
        row.Attempts.Should().Be(1);
        row.LastError.Should().Contain("JsonException");
    }

    [Fact]
    public async Task A_row_out_of_attempts_is_parked_and_stops_being_claimed()
    {
        await using var host = await OutboxTestHost.CreateAsync();
        var id = await host.EnqueueAsync(quoteId: 4);

        var publisher = new FlakyQuoteEventPublisher(failuresBeforeSuccess: 99);
        var relay = host.BuildRelay(publisher, new OutboxOptions { BatchSize = 10, MaxAttempts = 2 });

        await relay.RunOnceAsync(CancellationToken.None);   // attempt 1
        (await host.GetRowAsync(id)).Status.Should().Be(OutboxStatus.Pending);

        await relay.RunOnceAsync(CancellationToken.None);   // attempt 2 -- budget spent
        var parked = await host.GetRowAsync(id);
        parked.Status.Should().Be(OutboxStatus.Failed);
        parked.Attempts.Should().Be(2);

        var callsWhenParked = publisher.Calls;
        await relay.RunOnceAsync(CancellationToken.None);
        publisher.Calls.Should().Be(callsWhenParked, "a parked row must not keep consuming the batch");
    }

    [Fact]
    public async Task A_parked_row_does_not_hold_up_the_rows_behind_it()
    {
        await using var host = await OutboxTestHost.CreateAsync();
        await host.EnqueueAsync(quoteId: 1);   // will be parked (poison publisher)
        await host.EnqueueAsync(quoteId: 2);
        await host.EnqueueAsync(quoteId: 3);

        // Park the first row with a poison publisher, then let a healthy relay
        // take the rest. Head-of-line blocking is the failure this guards: one
        // undeliverable row must not stop every good row behind it.
        var poisonRelay = host.BuildRelay(
            new PoisonQuoteEventPublisher(),
            new OutboxOptions { BatchSize = 1, MaxAttempts = 5 });

        await poisonRelay.RunOnceAsync(CancellationToken.None);

        var publisher = new RecordingQuoteEventPublisher();
        var healthyRelay = host.BuildRelay(publisher);

        await healthyRelay.RunOnceAsync(CancellationToken.None);

        publisher.Published.Select(e => e.QuoteId).Should().BeEquivalentTo(new[] { 2, 3 });
    }

    // ------------------------------------------------------------------
    // Two relays, one outbox.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Two_relays_over_one_outbox_publish_every_row_exactly_once()
    {
        await using var host = await OutboxTestHost.CreateAsync();

        for (var quoteId = 1; quoteId <= 6; quoteId++)
            await host.EnqueueAsync(quoteId);

        var first = new RecordingQuoteEventPublisher();
        var second = new RecordingQuoteEventPublisher();

        // Distinct DI containers, so distinct LockOwners: two instances of the
        // app, not one instance twice.
        var relayA = host.BuildRelay(first, new OutboxOptions { BatchSize = 3, MaxAttempts = 3 });
        var relayB = host.BuildRelay(second, new OutboxOptions { BatchSize = 3, MaxAttempts = 3 });

        await relayA.RunOnceAsync(CancellationToken.None);
        await relayB.RunOnceAsync(CancellationToken.None);

        var all = first.Published.Concat(second.Published).Select(e => e.QuoteId).ToList();

        all.Should().HaveCount(6, "no row published twice");
        all.Should().OnlyHaveUniqueItems();
        all.Should().BeEquivalentTo(new[] { 1, 2, 3, 4, 5, 6 }, "and none skipped");

        await using var db = host.NewContext();
        (await db.OutboxMessages.CountAsync(m => m.Status == OutboxStatus.Sent)).Should().Be(6);
    }
}
