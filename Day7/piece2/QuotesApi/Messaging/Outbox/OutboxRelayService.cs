using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Observability;
using QuotesApi.Services;

namespace QuotesApi.Messaging.Outbox;

/// <summary>
/// Claims pending outbox rows, publishes them, marks them Sent.
///
/// This is the only thing in the application that talks to the broker on the
/// write side. After Day 20 no endpoint holds an IQuoteEventPublisher at all,
/// which is the observable proof that the request path and the broker are
/// decoupled rather than merely described as decoupled.
///
/// ORDER OF THE TWO STEPS IS THE WHOLE DESIGN. Publish, then mark. Reversed --
/// mark first, then publish -- a crash in the gap would lose the message,
/// which is the bug this class exists to remove. In this order a crash in the
/// gap republishes, and the consumer's (MessageId, SubscriptionName) primary
/// key absorbs it. Losing a message is unrecoverable; a duplicate is a row
/// that already exists. The asymmetry is why at-least-once is the correct
/// target and exactly-once is not on offer.
/// </summary>
public sealed class OutboxRelayService(
    IServiceScopeFactory scopeFactory,
    IOutboxSignal signal,
    IOptions<OutboxOptions> options,
    OutboxMetrics metrics,
    IClock clock,
    ILogger<OutboxRelayService> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    /// <summary>
    /// Identifies this relay in LockOwner. Machine plus process id plus a
    /// per-start suffix: two containers from the same image share a machine
    /// name, and a restarted process must not be mistaken for its predecessor
    /// whose leases are still in the table.
    /// </summary>
    private readonly string _owner = Truncate(
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}", 64);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Outbox relay started as {LockOwner} (batch {BatchSize}, poll {PollInterval}, lease {LeaseDuration})",
            _owner, _options.BatchSize, _options.PollInterval, _options.LeaseDuration);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Drain: keep going while a full batch comes back, so a
                // backlog is cleared at the speed of the broker rather than
                // one batch per poll interval.
                int dispatched;
                do
                {
                    dispatched = await RunOnceAsync(stoppingToken);
                }
                while (dispatched == _options.BatchSize && !stoppingToken.IsCancellationRequested);

                await RefreshBacklogAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A tick that throws must not kill the relay. A dead relay is
                // the one failure mode this design introduces, and it would
                // be invisible: every write would keep succeeding and nothing
                // would ever publish.
                logger.LogError(exception, "Outbox relay tick failed. Continuing after the poll interval.");
            }

            if (stoppingToken.IsCancellationRequested)
                break;

            await signal.WaitAsync(_options.PollInterval, stoppingToken);
        }

        logger.LogInformation("Outbox relay stopping");
    }

    /// <summary>
    /// One claim-publish-mark pass. Returns how many rows were dispatched, so
    /// the caller can tell a cleared backlog from a full batch.
    ///
    /// Public rather than private so the tests can drive exactly one pass and
    /// assert on the row afterwards. The alternative -- starting the service
    /// and waiting for a poll interval -- would make every crash-recovery test
    /// a timing race, and a flaky test that guards a durability property is
    /// worse than no test at all.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IQuoteEventPublisher>();

        var claimed = await ClaimBatchAsync(db, cancellationToken);

        if (claimed.Count == 0)
            return 0;

        foreach (var row in claimed)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Leave the row claimed and pending. The lease expires and
                // another tick (or another instance) takes it. Nothing is
                // lost by stopping here, which is the point of the lease.
                break;
            }

            await DispatchAsync(db, publisher, row, cancellationToken);
        }

        return claimed.Count;
    }

    /// <summary>
    /// Claims rows with an optimistic conditional UPDATE, checked for
    /// rows-affected == 1.
    ///
    /// Not "SELECT ... FOR UPDATE SKIP LOCKED" / "WITH (UPDLOCK, READPAST)":
    /// SQLite has no equivalent, so a claim built on it could only ever be
    /// exercised in the Docker-gated SQL Server suite -- which is precisely
    /// where nobody runs it in a feedback loop. This form is correct on both
    /// providers and on any future one.
    ///
    /// The shortlist read is an OPTIMISATION; the conditional UPDATE is the
    /// guarantee. Same split as IProcessedMessageStore's cheap HasSeenAsync in
    /// front of a primary-key constraint: two relays can both read the same
    /// candidate, and exactly one UPDATE reports a row changed.
    /// </summary>
    private async Task<List<OutboxMessage>> ClaimBatchAsync(
        QuotesDbContext db,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow.UtcDateTime;
        var leaseUntil = now.Add(_options.LeaseDuration);

        var candidates = await db.OutboxMessages
            .AsNoTracking()
            .Where(m => m.Status == OutboxStatus.Pending
                        && (m.LockedUntilUtc == null || m.LockedUntilUtc < now))
            .OrderBy(m => m.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        var claimed = new List<OutboxMessage>(candidates.Count);

        foreach (var candidate in candidates)
        {
            var affected = await db.OutboxMessages
                .Where(m => m.Id == candidate.Id
                            && m.Status == OutboxStatus.Pending
                            && (m.LockedUntilUtc == null || m.LockedUntilUtc < now))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(m => m.LockedUntilUtc, leaseUntil)
                        .SetProperty(m => m.LockOwner, _owner)
                        // Incremented on CLAIM, not on failure: this counts
                        // deliveries attempted, which is what the retry budget
                        // is actually about. A relay killed after claiming but
                        // before publishing has consumed an attempt, and
                        // should have, or a row that reliably kills the
                        // process would retry forever.
                        .SetProperty(m => m.Attempts, m => m.Attempts + 1),
                    cancellationToken);

            if (affected == 1)
            {
                candidate.LockedUntilUtc = leaseUntil;
                candidate.LockOwner = _owner;
                candidate.Attempts += 1;
                claimed.Add(candidate);
            }
            else
            {
                logger.LogDebug(
                    "Outbox row {OutboxId} was claimed by another relay before {LockOwner} could take it",
                    candidate.Id, _owner);
            }
        }

        return claimed;
    }

    private async Task DispatchAsync(
        QuotesDbContext db,
        IQuoteEventPublisher publisher,
        OutboxMessage row,
        CancellationToken cancellationToken)
    {
        QuoteChangedEvent evt;

        try
        {
            evt = OutboxPayload.Deserialize(row.Payload);
        }
        catch (Exception exception)
        {
            // A payload this process cannot read will not become readable on
            // the next tick. Park it and move on rather than spending the
            // retry budget and the batch on it.
            await ParkAsync(db, row, exception, "payload could not be read", cancellationToken);
            return;
        }

        // The span the stored traceparent exists for. Parented explicitly, so
        // a trace runs request -> outbox -> publish -> consumer handler across
        // a gap of minutes and a boundary of two processes. Started as a
        // Producer activity because the SDK's own send span will nest inside
        // it.
        using var activity = QuotesActivitySource.Instance.StartActivity(
            "Outbox publish",
            ActivityKind.Producer,
            parentId: row.TraceParent);

        activity?.SetTag("outbox.id", row.Id);
        activity?.SetTag("messaging.message.id", row.MessageId);
        activity?.SetTag("outbox.event_type", row.EventType);
        activity?.SetTag("outbox.attempts", row.Attempts);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // CancellationToken.None, not the stopping token: the row is
            // claimed and the change is committed. A shutdown that cancels a
            // send already in flight buys nothing -- the message either
            // arrived or it did not, and either way the row stays pending and
            // is retried. Passing the stopping token would additionally turn
            // every shutdown into a burst of OperationCanceledException noise
            // in the logs.
            await publisher.PublishAsync(evt, CancellationToken.None);

            stopwatch.Stop();
            metrics.RecordPublishDuration(stopwatch.Elapsed.TotalMilliseconds);

            await MarkSentAsync(db, row, cancellationToken);

            metrics.RecordPublished();
            activity?.SetStatus(ActivityStatusCode.Ok);

            logger.LogInformation(
                "Outbox row {OutboxId} published {EventType} as {MessageId} on attempt {Attempts} in {ElapsedMilliseconds} ms",
                row.Id, row.EventType, row.MessageId, row.Attempts, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            metrics.RecordFailure();
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);

            // Reuse Day 19's classifier rather than a fresh heuristic in this
            // catch block. Poison on the producer side is the same idea as
            // poison on the consumer side, and having one rule in one
            // testable place is why that class exists.
            var poison = MessageFailureClassifier.IsPoison(exception);
            var budgetSpent = row.Attempts >= _options.MaxAttempts;

            if (poison || budgetSpent)
            {
                await ParkAsync(
                    db, row, exception,
                    poison ? MessageFailureClassifier.PoisonReason(exception) : "retry budget exhausted",
                    cancellationToken);
                return;
            }

            // Release the lease so the next tick retries immediately rather
            // than waiting it out. Status stays Pending: the message is not
            // lost, it is simply not sent yet.
            await ReleaseAsync(db, row, exception, cancellationToken);

            logger.LogWarning(
                exception,
                "Outbox row {OutboxId} failed to publish on attempt {Attempts} of {MaxAttempts}. Will retry.",
                row.Id, row.Attempts, _options.MaxAttempts);
        }
    }

    private async Task MarkSentAsync(
        QuotesDbContext db,
        OutboxMessage row,
        CancellationToken cancellationToken)
    {
        // ExecuteUpdate rather than tracking and SaveChanges: one statement,
        // no entity to keep consistent, and no chance of writing back a stale
        // Attempts value read before the claim.
        //
        // If THIS throws, or the process dies before it runs, the row stays
        // Pending and will be published a second time. That is the documented
        // duplicate path, and the consumer's composite primary key is what
        // makes it a non-event.
        await db.OutboxMessages
            .Where(m => m.Id == row.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.Status, OutboxStatus.Sent)
                    .SetProperty(m => m.SentAtUtc, clock.UtcNow.UtcDateTime)
                    .SetProperty(m => m.LockedUntilUtc, (DateTime?)null)
                    .SetProperty(m => m.LockOwner, (string?)null)
                    .SetProperty(m => m.LastError, (string?)null),
                cancellationToken);
    }

    private async Task ReleaseAsync(
        QuotesDbContext db,
        OutboxMessage row,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await db.OutboxMessages
            .Where(m => m.Id == row.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.LockedUntilUtc, (DateTime?)null)
                    .SetProperty(m => m.LockOwner, (string?)null)
                    .SetProperty(m => m.LastError, Describe(exception)),
                cancellationToken);
    }

    private async Task ParkAsync(
        QuotesDbContext db,
        OutboxMessage row,
        Exception exception,
        string reason,
        CancellationToken cancellationToken)
    {
        await db.OutboxMessages
            .Where(m => m.Id == row.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.Status, OutboxStatus.Failed)
                    .SetProperty(m => m.LockedUntilUtc, (DateTime?)null)
                    .SetProperty(m => m.LockOwner, (string?)null)
                    .SetProperty(m => m.LastError, Describe(exception)),
                cancellationToken);

        metrics.RecordParked();

        // Error, not Warning: a parked row is a message that will never be
        // delivered without a human. That is the same severity as a
        // dead-lettered message, and should page the same way.
        logger.LogError(
            exception,
            "Outbox row {OutboxId} parked as Failed after {Attempts} attempts ({Reason}). "
            + "It will not be retried; {EventType} for MessageId {MessageId} was never published.",
            row.Id, row.Attempts, reason, row.EventType, row.MessageId);
    }

    /// <summary>
    /// Refreshes the two gauges. Cheap -- a COUNT and a MIN over a filtered
    /// index -- and once per tick, not once per metrics scrape.
    /// </summary>
    private async Task RefreshBacklogAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();

        var pending = db.OutboxMessages.AsNoTracking()
            .Where(m => m.Status == OutboxStatus.Pending);

        var count = await pending.CountAsync(cancellationToken);

        var oldest = count == 0
            ? (DateTime?)null
            : await pending.MinAsync(m => m.OccurredAtUtc, cancellationToken);

        var age = oldest is null
            ? TimeSpan.Zero
            : clock.UtcNow.UtcDateTime - oldest.Value;

        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        metrics.SetBacklog(count, age.TotalSeconds);

        if (age > _options.PendingAgeWarningThreshold)
        {
            logger.LogWarning(
                "Outbox backlog is stale: {PendingCount} pending, oldest {OldestPendingAgeSeconds:F0}s old "
                + "(threshold {ThresholdSeconds:F0}s). Events are committed but not published.",
                count, age.TotalSeconds, _options.PendingAgeWarningThreshold.TotalSeconds);
        }
    }

    /// <summary>
    /// Releases this relay's leases on the way down, so a rolling restart does
    /// not leave its in-flight rows waiting out a full lease before another
    /// instance can take them. Best-effort: if it fails, lease expiry is still
    /// the backstop, which is why the lease exists rather than a boolean flag.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();

            var released = await db.OutboxMessages
                .Where(m => m.LockOwner == _owner && m.Status == OutboxStatus.Pending)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(m => m.LockedUntilUtc, (DateTime?)null)
                        .SetProperty(m => m.LockOwner, (string?)null),
                    cancellationToken);

            if (released > 0)
                logger.LogInformation("Released {ReleasedCount} outbox leases held by {LockOwner} on shutdown", released, _owner);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not release outbox leases on shutdown. They will expire after {LeaseDuration}.",
                _options.LeaseDuration);
        }
    }

    /// <summary>
    /// Exception type and message only, truncated to the column width.
    /// Never the payload: LastError is read in diagnostics output and must not
    /// become a second, unaudited copy of user content.
    /// </summary>
    private static string Describe(Exception exception) =>
        Truncate($"{exception.GetType().Name}: {exception.Message}", 512);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
