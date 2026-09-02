# Day 20 — Exercise answer

> Paste the outbox table + relay. Describe the crash scenario you tested and why
> no message is lost or duplicated (at-least-once + idempotent consumer).

Four parts, in that order.

---

## 1. The outbox table

`OutboxMessages`, as SQL Server actually creates it
(`QuotesApi.Migrations.SqlServer/Migrations/20260902061851_AddOutboxMessages.cs`):

```sql
CREATE TABLE OutboxMessages (
    Id             bigint IDENTITY(1,1) NOT NULL,   -- the only sequencer
    MessageId      nvarchar(128)  NOT NULL,         -- = QuoteChangedEvent.EventId
    EventType      nvarchar(50)   NOT NULL,         -- the subscription filter reads this
    SchemaVersion  nvarchar(16)   NOT NULL,
    Payload        nvarchar(max)  NOT NULL,         -- the event, frozen at write time
    TraceParent    nvarchar(64)       NULL,         -- W3C id of the originating request
    OccurredAtUtc  datetime2      NOT NULL,
    Status         nvarchar(16)   NOT NULL,         -- Pending | Sent | Failed
    Attempts       int            NOT NULL,
    LastError      nvarchar(512)      NULL,
    LockedUntilUtc datetime2          NULL,         -- claim lease
    LockOwner      nvarchar(64)       NULL,
    SentAtUtc      datetime2          NULL,
    CONSTRAINT PK_OutboxMessages PRIMARY KEY (Id)
);

CREATE UNIQUE INDEX IX_OutboxMessages_MessageId ON OutboxMessages (MessageId);
CREATE INDEX IX_OutboxMessages_Pending  ON OutboxMessages (Status, Id) WHERE [Status] = 'Pending';
CREATE INDEX IX_OutboxMessages_SentAtUtc ON OutboxMessages (SentAtUtc);
```

SQLite gets the same shape — `INTEGER PRIMARY KEY AUTOINCREMENT`, `TEXT`
columns, and the identical partial-index predicate, which SQLite accepts
verbatim because bracket-quoted identifiers are legal there too. One filter
string serves both providers.

### Four choices in that table worth defending

**`Id` is the sequencer, not `OccurredAtUtc`.** The relay claims and publishes in
`Id` order. Wall-clock timestamps tie at low resolution and skew between
instances, so ordering by them would be ordering by something that is not
ordered. Under a frozen test clock three events written in sequence share one
timestamp exactly — `Updating_and_deleting_each_enqueue_their_own_event` asserts
they still come back in insertion order.

**`MessageId` is unique.** It becomes the broker's `MessageId`, and it is a
deterministic hash of the event, so two rows carrying it would be one logical
event enqueued twice. The database refuses rather than a code path hoping.

**`Payload` is a snapshot, and uncapped.** The event is serialised at write time,
not re-derived at publish time: re-deriving would publish whatever the row looks
like *now*, so two quick updates would emit the later state twice and the earlier
state never. Uncapped because a column that silently truncated an event body
would be a poison message the producer manufactured for itself.

**The pending index is filtered.** The claim query runs every tick for ever.
Unfiltered, that index grows with the full history of every event ever
published and the claim gets slower every day the app stays up. Filtered, it
stays proportional to the backlog, which is normally zero.

### On EF Core relationships: there is deliberately no relationship here

`OutboxMessage` has no foreign key to `Quotes` and no navigation property, and
that is the design rather than an omission. The obvious modelling instinct —
`OutboxMessage.QuoteId` as an FK — breaks on the third event type:

- `QuoteDeleted` describes a quote that no longer exists. An FK would refuse the
  insert, or `OnDelete(Cascade)` would delete the outbox row along with the
  quote, destroying the event that announces the deletion.
- An event is an immutable statement that something happened. Its lifetime is
  the retention window, not the aggregate's.
- The row already has to survive a contract change (`SchemaVersion`) and a
  process restart. Coupling it to a live row would make it the one part of the
  outbox that cannot outlive what it describes.

So the aggregate id travels inside `Payload`, and `QuoteId` is not a column at
all. The relationships that *are* modelled sit on the consumer side, where
`ProcessedMessage` uses a composite primary key rather than a surrogate — see
part 4.

### The write path

The endpoint no longer knows the broker exists. `QuoteWriteService` owns one
transaction:

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);

var inserted = await repository.AddAsync(quote, ct);            // Id assigned here
var evt = QuoteChangedEvent.Created(inserted.Id, callerId, ...);
outbox.Enqueue(evt);                                            // stages, does not save
await db.SaveChangesAsync(ct);                                  // still inside the transaction

await transaction.CommitAsync(ct);                              // both rows, or neither
```

Two `SaveChangesAsync` calls inside one explicit transaction, because
`Quote.Id` is database-generated and `EventId` is a hash over it — the outbox
row cannot be built until after the insert. A single save would be atomic and
would not work.

`IOutboxWriter.Enqueue` has no `Save`, `Commit` or `Flush` in its contract. A
writer that saved on its own behalf could commit the intent to publish without
the domain change that justifies it, which is the mirror image of the bug being
fixed.

---

## 2. The relay

`QuotesApi/Messaging/Outbox/OutboxRelayService.cs`, a `BackgroundService`. One
pass, verbatim:

```csharp
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
```

### Claiming a batch

```csharp
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
```

Not `FOR UPDATE SKIP LOCKED` or `WITH (UPDLOCK, READPAST, ROWLOCK)`: SQLite has
neither, and this application runs on SQLite locally and in the fast test
suite. A claim built on row hints could only ever be exercised in the
Docker-gated SQL Server project — precisely where nobody runs it in a feedback
loop. The conditional `UPDATE` checked for rows-affected is correct on both
providers, and `Two_relays_over_one_outbox_publish_every_row_exactly_once`
tests it without Docker.

### Publish, then mark — in that order

```csharp
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
```

Reversed — mark first, publish second — a crash in the gap would lose the
message, which is the bug this class exists to remove. In this order a crash in
the gap republishes. That asymmetry is the whole argument, and part 4 is why the
duplicate is affordable.

The publisher had to change to make this work: `ServiceBusQuoteEventPublisher`
used to catch every send failure and log it, which was the least-bad choice when
the publish ran on the request path. It now throws, because a publisher that
swallows the exception would report success, the relay would mark the row
`Sent`, and the message would be lost by exactly the mechanism built to prevent
that.

---

## 3. The crash scenario tested

### The scenario, run for real

`Day20/scripts/verify-crash-recovery.ps1`. The API starts with a **120-second**
poll interval, which opens a window where the row is provably committed and
provably unpublished:

```
[2/6] Creating a quote
      quote id 21 created (HTTP 201)
[3/6] Confirming the event is committed and NOT published
{ "counts": { "Pending": 1 }, "pendingCount": 1,
  "oldestPendingUtc": "2026-09-02T05:57:16.7951887",
  "oldestPendingAgeSeconds": 0.0688049, "parked": [] }

[4/6] Killing the process (no graceful shutdown)      <- Stop-Process -Force
[5/6] Restarting, with a 2 s poll interval. No other action.
[6/6] Waiting for the relay to drain what the dead process left behind
{ "counts": { "Sent": 1 }, "pendingCount": 0,
  "oldestPendingUtc": null, "oldestPendingAgeSeconds": 0, "parked": [] }

PASSED
```

**Step 3 is what makes this a proof.** A run that ends at step 6 with "the
message arrived" cannot distinguish recovery from a publish that happened on
time. The script aborts if fewer than one row is pending, rather than
continuing to a conclusion it has not earned.

**Step 4 is a real kill, not a thrown exception.** `Stop-Process -Force` gives
no unwinding, no `finally` blocks, no lease release, no flush. A thrown
exception keeps the CLR alive and would let an in-memory retry finish the job —
which would prove nothing about durability.

**Step 5 takes no other action.** No replay command, no manual fix. The intent
survived the crash because it is a database row.

The script is `../scripts/verify-crash-recovery.ps1` and takes no arguments —
it builds, starts the API, mints its own token, and asserts the pending row
before it kills anything, so the run either earns its `PASSED` or aborts.

Transcript: `../verification/day20-crash-recovery-run.txt`. Captures:
`../verification/screenshots/01-committed-not-published.png` and
`02-after-kill-restart.png` (a later run of the same script, so its counts read
`Sent: 4` → `Sent: 5` — different database, same behaviour).

### Every crash point, and what absorbs it

| Crash point | State left behind | What recovers it |
|---|---|---|
| Before the transaction commits | No quote, no outbox row | Nothing to recover — the write never happened |
| After commit, before the relay runs | Quote saved, row `Pending` | Relay publishes on its next pass (**the scenario above**) |
| Inside `SendMessageAsync` | Row `Pending`, one attempt spent, lease released | Relay retries |
| After the send, before "mark sent" | Row `Pending`, message already at the broker | Relay republishes — a duplicate, absorbed by part 4 |
| Relay killed holding a claim | Row `Pending` with a stale lease | `LockedUntilUtc` expires; the next pass reclaims it |

Each row is a test, not a narration:

| Crash point | Test |
|---|---|
| Publish throws | `Publish_failure_leaves_the_row_pending_and_costs_one_attempt` |
| Transient outage clears | `A_transient_outage_delays_the_message_but_never_loses_it` |
| **Crash after the send** | `A_crash_after_the_send_republishes_rather_than_losing_the_message` |
| Killed holding a claim | `An_expired_lease_is_reclaimed_so_a_killed_relay_blocks_nothing` |
| Enqueue fails | `A_failed_enqueue_takes_the_quote_down_with_it` |
| Restart after success | `Restarting_the_relay_does_not_republish_what_the_previous_one_sent` |

Each drives one pass with `RunOnceAsync` rather than starting the service and
waiting: a flaky test guarding a durability property is worse than no test, and
a poll interval inside an assertion is a race.

---

## 4. Why no message is lost, and why none is duplicated

These are two different guarantees with two different mechanisms, and it is
worth not blurring them.

### No message is lost — the transaction

The domain change and the intent to publish are in one transaction, so
"committed change, no message" is not a reachable state. Either both rows are
durable or neither is.

Tested in both directions, which matters — the second half is the one usually
skipped:

- `Creating_a_quote_commits_the_quote_and_its_event_together` — one quote row,
  one `Pending` outbox row, with a `MessageId` equal to
  `QuoteChangedEvent.BuildEventId("QuoteCreated", quoteId, occurredAt)`.
- `A_failed_enqueue_takes_the_quote_down_with_it` — force the enqueue to throw,
  assert **no quote exists**. If the quote survived a failed enqueue the
  transaction would be decorative, and the API would be back to committing
  changes whose events nobody will send.

And `No_request_path_publishes_to_the_broker` asserts the negative that makes
the rest structural: a publisher that throws if anything calls it is wired in,
all three write endpoints are exercised, all three succeed, three outbox rows
exist. Nothing on the request path can reach the broker, so no request can
commit a change whose event is then lost.

### Messages *are* duplicated — at-least-once is the honest ceiling

Publishing and marking-sent are two systems with no transaction between them.
A crash in that gap republishes on restart. No arrangement of those two steps
avoids it: mark-then-publish loses messages, publish-then-mark duplicates them,
and losing is unrecoverable while a duplicate is a row the consumer already
has. So the delivery guarantee is **at-least-once**, deliberately.

`A_crash_after_the_send_republishes_rather_than_losing_the_message` asserts the
duplicate happens, and asserts both copies carry the same `EventId`. It is a
test that the design's cost is paid as designed, not a test that the cost is
absent.

### The *effect* is not duplicated — the idempotent consumer

At-least-once is only safe because the consumer half already existed from
Day 19, and the two pieces were built to fit:

**A deterministic `MessageId`.** `QuoteChangedEvent.EventId` is a SHA-256 over
`(eventType, quoteId, occurredAtUtcTicks)`, formatted as a GUID. The same
logical event yields the same id after a process restart, not merely within one
process — which is exactly the case a crash produces. A `Guid.NewGuid()` at send
time would make every redelivery look like a new message and the dedupe store
useless.

**A primary key, not a check.** `ProcessedMessages` is keyed on
`(MessageId, SubscriptionName)`:

- Composite, because two subscriptions receive the same `MessageId` from one
  publish and they are different pieces of work. A single-column key would let
  the audit handler's row suppress the search-index handler's.
- The guarantee is the **constraint**, not the read. Two concurrent handlers can
  both read "not seen" and both proceed; the second `INSERT` violates the
  primary key, and the processor treats that specific error as "already done".
  `HasSeenAsync` in front of it is a cheap optimisation and must not be mistaken
  for the guarantee.
- The insert happens in the same transaction as the side effect, so "the work
  was done" and "the work was recorded as done" commit together — the same
  argument as part 1, one layer down.

So the chain is: the outbox makes the message inevitable, the deterministic id
makes the redelivery recognisable, and the composite primary key makes the
second delivery a no-op. **At-least-once delivery, exactly-once effect.**

### What this does not claim

- **Not exactly-once delivery.** Nothing here provides it and nothing here
  pretends to.
- **No ordering guarantee.** Rows are claimed and published in `Id` order, but
  with more than one relay instance there is no global or per-quote ordering at
  the broker. If per-aggregate ordering is ever needed, the answer is a
  session-enabled subscription with `SessionId = quoteId`, not a bigger lock.
- **The exactly-once effect was asserted by tests, not observed end to end.**
  The crash proof ran with `ServiceBus:Enabled=false`, so the relay published
  through the no-op publisher; every outbox state transition is identical, but
  no real consumer ran. `-WithServiceBus` with the emulator would close that,
  and is the obvious follow-up.
- **A new failure mode was introduced.** "Committed change, no message" is gone;
  "the relay is dead and every write still succeeds silently" is now possible,
  and it raises no error anywhere. That is what `outbox.oldest_pending.age` and
  the warning past `PendingAgeWarningThreshold` are for. A pending *count* is
  spiky under load and makes a bad alert; a row pending for minutes is
  unambiguous.
