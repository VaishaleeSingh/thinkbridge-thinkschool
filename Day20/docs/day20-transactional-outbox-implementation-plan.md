# Day 20 — The transactional outbox

## Detailed task prompt

> A DB write and a queue publish must not diverge. Implement the transactional
> outbox: write the domain change + an outbox row in one EF transaction, then a
> relay publishes and marks sent. Prove no message is lost if the publish step
> crashes.

## Goal

Close the gap Day 19 documented and deliberately left open.

Today `POST /api/quotes` commits the quote, and *then* calls
`IQuoteEventPublisher.PublishAsync`, which swallows every exception:

```csharp
// QuoteEndpointExtensions.cs, line ~155
var created = await repository.AddAsync(quote, cancellationToken);

// Publish AFTER commit. Not atomic with the database write:
// a crash here loses the event.
await publisher.PublishAsync(evt, CancellationToken.None);
```

`ServiceBusQuoteEventPublisher` logs that loss at Error and says out loud that
"this event is lost unless replayed from an outbox". Day 20 builds that outbox.

After this change the only durable act on the write path is a single EF
transaction that contains **both** the domain change and the intent to publish.
Nothing else on the request path talks to the broker at all.

## What "no message is lost" actually means

Precision matters here, because the outbox is routinely oversold.

The outbox removes exactly one failure mode: **committed change, no message**.
It does not, and cannot, give exactly-once delivery. The relay publishes, then
marks the row sent — two separate systems, no distributed transaction between
them — so a crash *between* those two steps republishes on restart:

| Crash point | Outcome | Who absorbs it |
|---|---|---|
| Before the transaction commits | No quote, no outbox row | Nothing to absorb — the write never happened |
| After commit, before the relay runs | Quote saved, row pending | Relay publishes on its next tick |
| Inside `SendMessageAsync` | Row still pending | Relay retries |
| After send, before "mark sent" | Row still pending → **duplicate publish** | Day 19's `ProcessedMessages` dedupe |

So the guarantee delivered is **at-least-once, with atomic intent**. The reason
that is safe rather than merely honest is that Day 19 already built the other
half: a composite-primary-key idempotency store keyed on
`(MessageId, SubscriptionName)`, and a `MessageId` (`QuoteChangedEvent.EventId`)
that is a deterministic SHA-256 of `(eventType, quoteId, occurredAtTicks)` — the
same logical event yields the same id after a process restart, not just within
one process. The outbox is the producer half of a guarantee whose consumer half
already exists and already has tests.

## Repository analysis and implementation boundary

The application is carried forward in place in **`Day7/piece2`**; `DayN/` folders
from Day 17 on hold docs, infra and verification evidence only (Day 18 and Day 19
both followed this). Day 20 does the same:

- Code, tests and migrations → `Day7/piece2`
- Plan, submission, verification evidence → `Day20/`

What is already in place and will be reused rather than rebuilt:

| Piece | Location | Reused for |
|---|---|---|
| `IQuoteEventPublisher` / `ServiceBusQuoteEventPublisher` | `QuotesApi/Messaging` | The relay's send step — unchanged interface |
| `QuoteChangedEvent` + deterministic `EventId` | `QuotesApi/Messaging` | The outbox row's `MessageId`, stable across restarts |
| `IProcessedMessageStore` (composite PK) | `QuotesApi/Messaging` | Consumer-side dedupe that makes at-least-once safe |
| `MessageFailureClassifier` | `QuotesApi/Messaging` | Deciding retry vs. park on the *producer* side too |
| `QueuedBackgroundJobService` (Day 18) | `QuotesApi/BackgroundJobs` | The `BackgroundService` shape the relay follows |
| Emulator collection fixture | `Quotes.Tests.Integration.ServiceBus` | End-to-end proof against a real broker |
| `IClock` | `QuotesApi/Services` | Deterministic time in relay tests |

The EF provider split matters: SQLite locally and in the in-process integration
suite, SQL Server in `Quotes.Tests.Integration.SqlServer` and in Azure, with
SQL Server migrations kept in the separate `QuotesApi.Migrations.SqlServer`
project. **Every claim mechanism below must work on both providers**, which
rules out the usual `WITH (UPDLOCK, READPAST)` / `FOR UPDATE SKIP LOCKED`
answer as the primary design (see "Claiming a batch").

## Architecture

### 1. The outbox table

`QuotesApi/Models/OutboxMessage.cs`, mapped in `QuotesDbContext`:

| Column | Type | Why |
|---|---|---|
| `Id` | `long`, identity | Insertion order. Claim and publish order by this, not by `OccurredAtUtc` — wall-clock ties and clock skew make timestamps a bad sequencer |
| `MessageId` | `nvarchar(128)`, **unique** | `QuoteChangedEvent.EventId`. The unique index is a real guard: two requests that somehow derive the same logical event cannot both enqueue it |
| `EventType` | `nvarchar(50)` | Set on `ApplicationProperties["eventType"]` — the subscription filter reads it. Stored as a column, not dug out of the JSON, so the relay never deserialises to route |
| `SchemaVersion` | `nvarchar(16)` | Same reason; also lets an old pending row be recognised after a contract change |
| `Payload` | `nvarchar(max)` | The serialised `QuoteChangedEvent`, **serialised at write time**. The relay sends bytes; it never re-derives the event from current state, which would publish a *later* state than the one that occurred |
| `TraceParent` | `nvarchar(64)`, null | W3C trace id of the originating request. Without this the trace breaks: the relay publishes minutes later on another thread, and the consumer span would have no parent |
| `OccurredAtUtc` | `datetime2` | Diagnostics and the pending-age metric |
| `Status` | `nvarchar(16)` | `Pending` / `Sent` / `Failed` |
| `Attempts` | `int` | Retry budget |
| `LastError` | `nvarchar(512)`, null | Truncated; exception type + message only, never payload |
| `LockedUntilUtc` | `datetime2`, null | Claim lease |
| `LockOwner` | `nvarchar(64)`, null | Which relay instance holds it — diagnostics only, never authorisation |
| `SentAtUtc` | `datetime2`, null | Retention sweep reads this |

Indexes:

- `UNIQUE (MessageId)`
- `(Status, Id)` — the claim query's access path. On SQL Server a **filtered**
  index `WHERE Status = 'Pending'` keeps it small forever; on SQLite a partial
  index with the same predicate. Without the filter this index grows with the
  full history of every event ever published, and the claim query degrades as
  the table ages.
- `(SentAtUtc)` — the retention sweep.

### 2. The write path — one transaction, and why it needs to be explicit

The natural instinct is "add both to the `DbContext`, call `SaveChangesAsync`
once — EF wraps a single `SaveChanges` in an implicit transaction". That does
not work here, and the reason is worth stating rather than discovering:
`Quote.Id` is database-generated, and `QuoteChangedEvent.EventId` is a hash
over `(eventType, quoteId, occurredAt)`. The outbox row cannot be built until
the quote's identity exists, which is *after* the insert.

So the transaction is explicit, and the two `SaveChangesAsync` calls sit inside
it:

```csharp
await using var tx = await db.Database.BeginTransactionAsync(ct);

db.Quotes.Add(quote);
await db.SaveChangesAsync(ct);          // Id assigned here

outbox.Enqueue(QuoteChangedEvent.Created(quote.Id, callerId, ...));
await db.SaveChangesAsync(ct);          // still inside tx

await tx.CommitAsync(ct);               // both, or neither
```

Two design consequences:

- **The endpoint must not own this.** Three endpoints (create, update, delete)
  each need the same shape. It goes behind one seam —
  `IQuoteWriteService` in `QuotesApi/Services`, holding
  `CreateAsync` / `UpdateAsync` / `DeleteAsync` — so the transaction boundary is
  written once and can be tested without HTTP. The endpoints keep their
  validation and their `Results.*` shaping and lose the publish call entirely.
- **`IQuoteEventPublisher` disappears from the endpoints' signatures.** After
  this change the request path has no reference to the broker. That is the
  observable proof the coupling is gone, and the plan's first acceptance
  criterion.

`IOutboxWriter.Enqueue(QuoteChangedEvent)` serialises the event, captures
`Activity.Current?.Id`, and `Add`s the row to the *same* `QuotesDbContext`
instance the caller is using. It deliberately does **not** call
`SaveChangesAsync` — an outbox writer that saves on its own behalf is an outbox
writer that can commit without the domain change, which is the exact bug being
fixed.

> **Execution strategy note.** `InfrastructureExtensions` currently calls
> `UseSqlServer(...)` with no `EnableRetryOnFailure`, so
> `BeginTransactionAsync` is safe today. The moment a retrying execution
> strategy is turned on, a manual transaction throws
> `InvalidOperationException` unless it is wrapped in
> `db.Database.CreateExecutionStrategy().ExecuteAsync(...)`. The plan writes it
> wrapped from the start — it costs one lambda and removes a landmine that
> would otherwise detonate in Azure and not locally.

### 3. The relay

`QuotesApi/Messaging/Outbox/OutboxRelayService : BackgroundService`, following
the Day 18 `QueuedBackgroundJobService` shape (scope per unit of work, structured
logging, cooperative shutdown).

Loop, per tick:

1. **Claim** a batch (below).
2. For each claimed row, in `Id` order: rebuild the `ServiceBusMessage` from the
   stored bytes — `MessageId = row.MessageId`, `Subject`/
   `ApplicationProperties["eventType"] = row.EventType`,
   `["schemaVersion"]`, `["traceparent"] = row.TraceParent` — and send it.
3. **Publish first, mark second.** `Status = Sent`, `SentAtUtc = now`, via
   `ExecuteUpdateAsync` (no tracking, one statement).
4. On failure: `Attempts++`, `LastError = type + message` (truncated), clear the
   lease so another tick can retry. If `Attempts >= MaxAttempts`, **or** the
   exception is poison by `MessageFailureClassifier.IsPoison`, set
   `Status = Failed` and leave it. A row that can never send must stop consuming
   the batch, or one bad row starves every good one behind it — the producer-side
   equivalent of the DLQ, and the reason Day 19's classifier is reused here
   rather than a fresh `catch`-block heuristic.

Ordering, stated honestly: rows are claimed and sent in `Id` order, but with
`MaxConcurrentPublishes > 1` or more than one relay instance there is **no**
global or per-quote ordering guarantee at the broker. If per-aggregate ordering
is ever required, the answer is a session-enabled subscription with
`SessionId = quoteId`, not a bigger lock. The submission will say this rather
than let a reader assume ordering it does not have.

### 4. Claiming a batch — provider-neutral, and why

Two relay instances (or one relay and one leftover lease) must not both publish
the same row. The textbook answer is `SELECT ... FOR UPDATE SKIP LOCKED`
(PostgreSQL) or `WITH (UPDLOCK, READPAST, ROWLOCK)` (SQL Server). SQLite has
neither, so a design that depends on it is a design that cannot be tested in the
fast in-process suite — it would only ever be exercised in the Docker-gated
SQL Server suite, which is precisely where nobody runs it in a feedback loop.

The claim is therefore **an optimistic conditional update**, which is correct on
both providers and on any future one:

```sql
UPDATE OutboxMessages
   SET LockedUntilUtc = @leaseUntil, LockOwner = @owner
 WHERE Id = @id
   AND Status = 'Pending'
   AND (LockedUntilUtc IS NULL OR LockedUntilUtc < @now)
```

Expressed as `ExecuteUpdateAsync(...)` and **checked for rows-affected == 1**.
Exactly one relay wins each row; the losers see 0 and skip. Candidates are read
first with `AsNoTracking()` (`Status = Pending`, lease free or expired, ordered
by `Id`, `Take(BatchSize)`) purely as a cheap shortlist — the shortlist is an
optimisation, the conditional `UPDATE` is the guarantee. This is the same
"cheap pre-check, constraint is the real guarantee" split Day 19 wrote into
`IProcessedMessageStore`, applied to the producer side.

A **lease**, not a boolean flag: a relay that is `kill -9`'d holding claimed rows
must not leave them claimed forever. `LockedUntilUtc` expires and the next tick
picks them up. That expiry is a large part of what the crash proof exercises.

### 5. Latency — poll, plus a nudge

A pure poll loop makes every event's latency a coin-flip up to the poll interval.
Sitting on a 5-second interval to make a demo look good, meanwhile, wastes a
database round-trip per instance per tick forever.

Both: an `IOutboxSignal` wrapping a bounded `Channel<byte>` (capacity 1, drop on
full). The write service signals it after a successful commit; the relay awaits
*either* the signal or the poll interval. Latency is milliseconds on the happy
path, and the poll remains the durable fallback that makes the signal purely an
optimisation — a dropped or missed signal costs one poll interval, never a lost
message. If the signal were the only trigger it would be a second, in-memory
publish path with all the durability the outbox exists to replace.

### 6. Configuration

New `Outbox` section (`OutboxOptions`, `ValidateDataAnnotations` +
`ValidateOnStart`, matching `ServiceBusOptions`):

| Key | Default | Meaning |
|---|---|---|
| `Outbox:RelayEnabled` | `false` | Whether the relay `BackgroundService` runs |
| `Outbox:PollInterval` | `00:00:05` | Fallback tick when no signal arrives |
| `Outbox:BatchSize` | `20` | Rows claimed per tick |
| `Outbox:LeaseDuration` | `00:01:00` | Claim lease; must exceed worst-case publish time for a batch |
| `Outbox:MaxAttempts` | `5` | Then `Failed` |
| `Outbox:RetentionDays` | `7` | Sent rows older than this are swept |

`RelayEnabled` is a **separate switch from `ServiceBus:Enabled`**, and that is
deliberate. The outbox row is written unconditionally — it is part of the domain
transaction, not part of messaging. The existing integration suite runs with
`ServiceBus:Enabled = false`, and with the relay off it can now assert something
it never could before: *the row is there, pending, and nothing consumed it*. If
the relay ran with the no-op publisher it would mark every row `Sent`
instantly and quietly destroy the evidence the tests need.

### 7. Retention

`OutboxRetentionService` — a low-frequency `BackgroundService` that deletes
`Status = Sent AND SentAtUtc < now - RetentionDays` via `ExecuteDeleteAsync`.

It sweeps `ProcessedMessages` on the same schedule, which closes a gap Day 19
wrote down and did not fix ("rows grow forever without a cleanup job"). One
constraint carries over verbatim and belongs in a comment: the retention window
must exceed message TTL + maximum DLQ dwell time, or a replayed message finds
its dedupe row already swept and the side effect repeats.

## Planned file changes

**New — `Day7/piece2/QuotesApi`**

```
Models/OutboxMessage.cs
Messaging/Outbox/IOutboxWriter.cs
Messaging/Outbox/EfOutboxWriter.cs
Messaging/Outbox/IOutboxSignal.cs
Messaging/Outbox/ChannelOutboxSignal.cs
Messaging/Outbox/OutboxRelayService.cs
Messaging/Outbox/OutboxRetentionService.cs
Messaging/Outbox/OutboxOptions.cs
Services/IQuoteWriteService.cs
Services/QuoteWriteService.cs          // the transaction boundary
Extensions/OutboxExtensions.cs         // DI wiring, mirrors MessagingExtensions
```

**Modified**

```
Data/QuotesDbContext.cs                       // DbSet + mapping + indexes
Extensions/QuoteEndpointExtensions.cs         // IQuoteEventPublisher removed from all 3
Extensions/DiagnosticsEndpointExtensions.cs   // GET /api/diagnostics/outbox
Program.cs                                    // AddOutbox(...)
appsettings.json / appsettings.Development.json
Migrations/ (SQLite) + QuotesApi.Migrations.SqlServer/Migrations/ (SQL Server)
```

**Tests**

```
Quotes.Tests.Unit/Messaging/Outbox/OutboxRelayServiceTests.cs
Quotes.Tests.Unit/Messaging/Outbox/OutboxClaimTests.cs
Quotes.Tests.Integration/OutboxAtomicityTests.cs
Quotes.Tests.Integration/OutboxCrashRecoveryTests.cs
Quotes.Tests.Integration.SqlServer/OutboxConcurrencyTests.cs
Quotes.Tests.Integration.ServiceBus/OutboxEndToEndTests.cs
```

**Docs / evidence — `Day20/`**

```
docs/day20-transactional-outbox-implementation-plan.md   (this file)
docs/day20-transactional-outbox-submission.md
verification/screenshots/
scripts/crash-relay.ps1                                   // the kill -9 proof
```

## Implementation sequence

1. `OutboxMessage` + mapping + both migrations. Verify the SQL Server migration
   actually emits the filtered index — EF is quiet about `HasFilter` mistakes.
2. `IOutboxWriter` / `EfOutboxWriter`. Unit test: enqueue does **not** save.
3. `QuoteWriteService` with the explicit transaction, wrapped in the execution
   strategy. Move create / update / delete onto it.
4. Rewire the three endpoints; delete the `IQuoteEventPublisher` parameters.
   Existing endpoint tests should pass untouched — if they do not, behaviour
   changed and that is a bug, not a test to edit.
5. `OutboxRelayService` with claim, publish, mark, retry, park.
6. `IOutboxSignal` and the nudge.
7. `OutboxRetentionService`.
8. Diagnostics endpoint + metrics.
9. Crash tests, then the manual kill-the-process run for evidence.
10. Submission doc written against a green run, not against intent.

## Test strategy — and how the crash proof actually works

### Unit (no broker, no database)

- Claim honours an unexpired lease held by someone else; claims one whose lease
  has expired.
- Publish throws → `Attempts` incremented, `Status` still `Pending`, lease
  cleared.
- `Attempts` at `MaxAttempts` → `Failed`, and the next tick skips it.
- A poison exception (`JsonException`) → `Failed` on the **first** attempt, no
  retry budget burned.
- Send succeeds, mark throws → row stays `Pending` (this is the duplicate-
  producing path, asserted as *intended* behaviour rather than left implicit).

### Integration, in-process SQLite (`Quotes.Tests.Integration`)

- **Atomicity, forward:** `POST /api/quotes` → exactly one `Quotes` row and
  exactly one `Pending` `OutboxMessages` row with a matching deterministic
  `MessageId`.
- **Atomicity, backward:** force the outbox insert to fail (a writer double
  registered to violate the unique index) → assert **no** quote row exists.
  This is the half that is usually skipped, and it is the half that proves the
  transaction is real rather than incidental.
- **Crash before publish:** relay off, POST, assert row pending. Start a relay
  with a working fake publisher. Assert published exactly once and marked
  `Sent`.
- **Crash *during* publish:** a `CrashingQuoteEventPublisher` throws on the
  first N calls. Assert the row survives, then succeeds, and the total
  publish count matches the number of enqueued events — no loss.
- **Crash *after* publish, before mark:** the strongest one. A publisher that
  records the send and then throws *from the mark step*. Assert the row is
  re-published on the next tick (a duplicate, correctly), and that the
  consumer's `ProcessedMessages` row keeps the *side effect* at exactly one.
  This is the test that shows the outbox and Day 19's idempotency store are one
  mechanism, not two features.

### Integration, SQL Server (`Quotes.Tests.Integration.SqlServer`, Docker)

- Two relay instances against one database, 200 pending rows: every row is
  published **exactly once** across both relays, and the union of both instances'
  work is the full set. This is the test that would silently pass with a broken
  claim on SQLite's serialised writes, which is why it belongs on the real
  provider.

### End-to-end, emulator (`Quotes.Tests.Integration.ServiceBus`, Docker)

- POST through HTTP, relay on, real emulator: assert `QuoteAuditEntries` and
  `QuoteSearchProjections` receive the event, and the outbox row reaches `Sent`.

### Manual — the actual kill

Automated tests simulate a crash by throwing. A thrown exception is not a
process death, and the submission should not pretend it is. So, scripted and
screenshotted:

1. Relay poll interval set to 60s, `RelayEnabled = true`, emulator running.
2. `POST /api/quotes` → 201.
3. Confirm: quote row present, outbox row `Pending`, **nothing on the topic**.
4. `Stop-Process -Force` the API (real `SIGKILL`, mid-window, no graceful
   shutdown, no flush).
5. Restart the API. Take no other action.
6. Assert: the event arrives at both subscriptions, the outbox row is `Sent`,
   and the audit/projection rows exist exactly once.

Step 3 is the part that carries the argument: it shows the message genuinely had
not been published at the moment the process died, so step 6 is recovery rather
than a race that happened to resolve. Run the same sequence against `main` to
show the event is lost — the before/after is what makes the claim checkable.

## Observability

- Metrics: `outbox.pending.count`, `outbox.oldest_pending.age_seconds`,
  `outbox.published.count`, `outbox.failed.count`, `outbox.publish.duration`.
  Oldest-pending age is the one to alert on: pending count is naturally spiky,
  but a row that has been pending for ten minutes means the relay is dead or
  wedged, and that is the *only* condition under which this design silently
  stops delivering.
- `GET /api/diagnostics/outbox` (same authorization as the existing diagnostics
  routes): counts by status and the oldest pending age. Also what the manual
  verification reads at step 3.
- The relay starts an `Activity` per row with `TraceParent` as its parent, so a
  trace runs request → outbox → publish → consumer handler, across a minutes-long
  gap and two processes.

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| Relay dies; writes keep succeeding; nothing publishes and nothing errors | Oldest-pending-age metric + alert. This is the failure mode the design creates in exchange for the one it removes, and it must be monitored, not assumed away |
| Payload is a frozen snapshot; a contract change makes old pending rows unreadable | `SchemaVersion` stored per row; the relay parks a row it cannot build rather than crash-looping the batch |
| Table grows unbounded | Retention service + filtered index so the claim path never scans history |
| Long lease + many instances = idle relays | Lease sized to worst-case batch publish time, not to a round number; batch size small |
| `EnableRetryOnFailure` turned on later breaks the manual transaction | Execution strategy wrapper written now, before it is needed |
| Two `SaveChanges` inside one transaction reads as accidental | Comment at the transaction boundary explaining the database-generated `Id` forces it |
| The relay outruns the broker under a backlog burst | `MaxConcurrentPublishes` bounded; Service Bus throttling is transient, so it retries by design |

## Acceptance criteria

1. No endpoint on the write path references `IQuoteEventPublisher`.
2. Quote row and outbox row are committed in one transaction; killing either
   half leaves neither.
3. A relay that crashes at any point publishes the event after restart, with no
   manual intervention, and the consumer's side effect happens exactly once.
4. A poison row parks as `Failed` without blocking rows behind it.
5. The whole suite is green with the relay off, so nothing new depends on Docker
   to pass CI.
6. The SQL Server suite proves two relays never double-publish a row.
7. The submission shows the before (event lost on `main`) and the after
   (event recovered), from a real `SIGKILL`, with evidence.
