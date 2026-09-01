# Day 19 — Azure Service Bus topics, competing consumers, idempotency and the DLQ

## Result

Quote writes now publish a `QuoteChangedEvent` to a Service Bus topic with two
subscriptions. Both are consumed in-process by `BackgroundService` workers that
settle every message explicitly, record what they handled so a redelivery cannot
repeat the work, and dead-letter a payload that can never succeed instead of
retrying it three times first.

Verified on 1 September 2026, .NET 10, against the Azure Service Bus emulator
and a containerised SQL Server:

```text
dotnet test Day7/piece2/QuotesApi.slnx
  total: 198, failed: 0, succeeded: 198

dotnet test Day7/piece2/Quotes.Tests.Integration.ServiceBus
  total: 5, failed: 0, succeeded: 5
```

Log lines quoted below are copied verbatim from that run.

## Why the implementation lives in Day 7

The maintained backend is `Day7/piece2/QuotesApi`; Days 11, 12 and 18 added
their lessons there in place and kept documentation in their own folder. Day 19
follows that. Copying the API into `Day19/piece2` would fork an app whose auth,
telemetry and tests keep evolving, and make the messaging diff unreadable.

## What Day 18 said, and what changed

Day 18's own risks section recorded two limits of its in-memory channel:
accepted work is lost on restart, and multiple replicas have isolated queues. A
broker is the answer to both, and Day 19 does not delete those sentences — it
answers them. The background-job queue stays exactly as it was; it is the right
shape for work that belongs to one process.

## Design

### The topology is infrastructure, not startup code

`Day19/infra/servicebus.bicep` declares the namespace, topic `quote-events`, and
subscriptions `audit` and `search-index`. An app that creates its own topics
needs `Manage` rights in production, which is precisely the right it should not
have: it gets `Data Sender` on the topic and `Data Receiver` on the
subscriptions, through managed identity, with `disableLocalAuth: true` and no
connection string anywhere in configuration.

Two settings are pinned against their defaults, with reasons:

- `MaxDeliveryCount: 3`, not the service default of 10. Ten attempts hide a
  poison message behind a wall of retries.
- `RequiresDuplicateDetection: false`, deliberately — see idempotency below.

The `search-index` subscription's SQL filter is
`eventType IN ('QuoteCreated','QuoteUpdated')`. Adding that rule does **not**
remove the `$Default` TrueFilter that ARM creates, and a subscription with both
matches everything, because rules are OR'd. The Bicep overwrites `$Default` with
`1=0` so the effective filter is the SQL rule alone. This is the single most
common "my filter does nothing" bug, and the fan-out test fails loudly if it
ever comes back.

### Fan-out is a filter, not a second publish

One publish, two subscriptions, different slices. `audit` records every event;
`search-index` maintains a projection and never sees a delete. Publishing twice
would prove nothing about the broker.

Evidence: one `MessageId` appearing under both `Subscription=audit` and
`Subscription=search-index`, and a delete appearing under `audit` only.

### The message carries data, never request state

`QuoteChangedEvent` is an immutable record — quote id, owner id, occurred-at,
author, text, `schemaVersion`. Not the EF entity: serialising that would ship
navigation properties and internal columns across what is now a public contract.

On the message itself:

- `MessageId` = a deterministic event id (SHA-256 over event type, quote id and
  occurred-at ticks), **not** `Guid.NewGuid()` at send time. A send retried by
  the SDK's own retry policy must not become two distinct ids.
- `ApplicationProperties["eventType"]` — the property the subscription filter
  matches on. Service Bus filters cannot read the body; only system and
  application properties are addressable.
- `ApplicationProperties["traceparent"]` — the current activity id, as a
  **string**. An `Activity`, `HttpContext`, `ClaimsPrincipal` or `DbContext` must
  never be placed in a message; Day 18's rule carries over and matters more here,
  because the payload now leaves the process.

### One worker per subscription

`QuoteEventProcessorService` takes its subscription name as a constructor
argument and is registered once per subscription, each instance owning its own
`ServiceBusProcessor`. The alternative — one worker plus a registered handler
nothing resolves — was in an earlier draft of this branch and is exactly the kind
of code that reads as a feature and executes as nothing.

Processor settings that matter:

- `AutoCompleteMessages = false`. This is the decision the exercise turns on.
  With auto-complete, "deduped a duplicate" and "handler quietly did nothing"
  produce identical outcomes. Explicit settlement makes complete, abandon and
  dead-letter each a line of code someone chose.
- `PeekLock`, stated rather than inherited: `ReceiveAndDelete` loses the message
  on any handler failure and makes both retries and the DLQ impossible.
- `MaxConcurrentCalls = 4`, which makes a single instance a competing consumer
  over its own subscription.
- `MaxAutoLockRenewalDuration`, so a slow handler does not finish work on an
  expired lock and then throw `MessageLockLost` on completion.
- `ProcessErrorAsync` is implemented, not omitted: it is the only place
  SDK-level faults (link failures, credential expiry, entity-not-found) surface.

## Idempotency

At-least-once delivery is the broker's contract. Redelivery is ordinary, not
exceptional: a lock expiry, an abandon, or a consumer crash all produce it. The
handler must therefore be safe to run twice — and "safe" has to be a property of
the database, not of a check.

### Why broker duplicate detection is not the answer

`RequiresDuplicateDetection` makes Service Bus discard a second message with the
same `MessageId` inside a window. It protects against a *publisher* sending
twice. It does nothing about redelivery, because redelivery is the broker
correctly delivering one message again. It is switched off here so that the
guarantee lives where it actually holds.

### Where the guarantee lives

A `ProcessedMessages` table keyed on `(MessageId, SubscriptionName)`. The key is
composite because both subscriptions receive the same message id from one
publish, and they are different pieces of work — a single-column key would let
the audit handler's row suppress the search-index handler's.

The handler contract, in one transaction on the scope's `DbContext`:

1. apply the side effect,
2. insert the `ProcessedMessages` row,
3. commit,
4. complete the message.

Both writes commit together or neither does. A cheap `HasSeen` pre-check runs
first, but it is an optimisation, not the guarantee: under `MaxConcurrentCalls > 1`
two consumers can both read "not seen". The loser of the insert race gets a
unique-constraint violation, rolls its side effect back, and completes the
message. Detection is by provider error code (`SqliteErrorCode == 19`,
`SqlException.Number` 2627/2601), not by matching exception text — message
strings are localised, and a substring search for "2627" matches any message
containing those four digits.

Steps 3 and 4 are still not atomic: a crash between them redelivers a message
whose work is done, and the dedupe row absorbs it on the next attempt. That is
the reason the store exists, not a flaw left in it.

Evidence: the same `MessageId` delivered twice produced
`Duplicate MessageId=… — completing without side effect` on both subscriptions,
and one audit row.

## Dead-lettering — two routes, and the difference between them

**Exhausting `MaxDeliveryCount`.** The handler abandons; after three deliveries
the broker moves the message to `…/audit/$DeadLetterQueue` with
`DeadLetterReason = MaxDeliveryCountExceeded`. Right for a failure that might be
transient: a database timeout, a downstream 503.

**Immediate `DeadLetterMessageAsync(reason, description)`.** The handler
classifies the failure as one repetition cannot fix — malformed JSON, an unknown
`schemaVersion` — and dead-letters on the first delivery with
`InvalidPayload`. Retrying a message that can never succeed burns the delivery
budget, delays everything behind it, and writes three identical error logs where
one would do.

The classification rule is one small testable function
(`MessageFailureClassifier`), not an inline `catch`. `DeadLetterErrorDescription`
is readable by anyone with receive rights, so it carries the exception type and
message and no user or token material.

Evidence: a non-JSON body dead-lettered at `DeliveryCount=1` with
`Reason=InvalidPayload`, asserted by reading it back off the DLQ.

A DLQ is a queue, not an alert. `GET /api/diagnostics/quote-events/dead-letters`
peeks it (Development-gated, ids and reasons only, never the body); the real
operational answer is an Azure Monitor alert on `DeadletteredMessages > 0`, and a
replay path re-enters the idempotent handler — which is why the dedupe retention
window must exceed the DLQ dwell time.

## The gap this design leaves open

Publishing happens after `SaveChangesAsync`, and the two are not atomic. A crash
between them loses the event; the database has the change and nobody is told. A
send failure is logged at error and does not fail an HTTP response for a write
that already succeeded, so the gap is quiet by design.

The fix is a transactional outbox: write the event to a table inside the same
transaction and let a relay publish it. It costs a relay, care about ordering,
and at-least-once publishing — which the consumer's idempotency already
tolerates. It is described here rather than built, and no part of this system
should be described as exactly-once.

## Configuration

```jsonc
"ServiceBus": {
  "Enabled": false,                    // off unless configured
  "FullyQualifiedNamespace": "",       // never a connection string
  "TopicName": "quote-events",
  "AuditSubscription": "audit",
  "SearchIndexSubscription": "search-index",
  "MaxConcurrentCalls": 4,
  "PrefetchCount": 0,
  "MaxAutoLockRenewalMinutes": 5
}
```

`Enabled: false` by default is load-bearing: the integration suite boots the real
`Program.cs`, and a processor opening an AMQP connection at startup would fail
every unrelated test on a machine with no namespace. Disabled, the publisher is a
no-op and no client, sender, processor or worker is registered — the same pattern
`ObservabilityExtensions` uses for the OTLP exporter.

There is deliberately no `MaxDeliveryCount` setting. It is a property of the
subscription, set in the Bicep; an app setting of that name would read like a
knob and turn nothing.

## Files changed

```text
Day7/piece2/QuotesApi/Messaging/          11 files: event, publisher (+ no-op),
                                          worker, handlers, dedupe store,
                                          failure classifier, options
Day7/piece2/QuotesApi/Extensions/         MessagingExtensions (registration),
                                          QuoteEndpointExtensions (publish),
                                          DiagnosticsEndpointExtensions (DLQ peek),
                                          ObservabilityExtensions (SDK spans)
Day7/piece2/QuotesApi/Models/             ProcessedMessage, QuoteAuditEntry,
                                          QuoteSearchProjection
Day7/piece2/QuotesApi/Migrations/         AddMessagingTables (SQLite)
Day7/piece2/QuotesApi.Migrations.SqlServer/ SyncModelThroughDay19
Day7/piece2/Quotes.Tests.Unit/Messaging/  event, classifier, store
Day7/piece2/Quotes.Tests.Integration/     QuoteEventPublishingTests
Day7/piece2/Quotes.Tests.Integration.ServiceBus/  emulator suite (new project)
Day19/                                    plan, this document, Bicep
```

## Verification

| Claim | How |
|---|---|
| Writes still work with messaging off | `QuoteEventPublishingTests` — POST/PUT/DELETE with the no-op publisher |
| One publish reaches both subscriptions | `One_event_is_processed_once_per_subscription` — two `ProcessedMessages` rows, one audit row |
| The filter drops deletes | `Search_index_subscription_never_sees_a_delete` — asserted on `ProcessedMessages`, not inferred |
| A redelivered message does the work once | `Same_message_delivered_twice_produces_one_audit_row` |
| A poison payload dead-letters immediately | `Malformed_payload_is_dead_lettered_on_first_delivery` — `InvalidPayload` at `DeliveryCount=1` |
| Event ids are deterministic | `QuoteEventPublisherTests` |
| Retryable vs poison classification | `MessageFailureClassifierTests` |
| The dedupe key is enforced by the database | `ProcessedMessageStoreTests` — real SQLite, not the InMemory provider, which does not enforce constraints |

### The run, in its own words

Fan-out — one publish, both subscriptions:

```text
[13:23:43 INF] Published QuoteCreated for quote 999 with MessageId fb57f8158e3baade4c00920c812f29c0
[13:23:43 INF] Processing MessageId=fb57f8158e3baade4c00920c812f29c0 DeliveryCount=1 Subscription=audit
[13:23:43 INF] Processing MessageId=fb57f8158e3baade4c00920c812f29c0 DeliveryCount=1 Subscription=search-index
[13:23:43 INF] Audit: recorded QuoteCreated for quote 999 (EventId=fb57f8158e3baade4c00920c812f29c0)
[13:23:43 INF] SearchIndex: upserted projection for quote 999 (EventType=QuoteCreated, EventId=fb57f8158e3baade4c00920c812f29c0)
```

The filter — quote 2001 created then deleted; the delete reaches `audit` only,
and no `Subscription=search-index` line exists for it:

```text
[13:23:43 INF] Processing MessageId=2e89f7622777c0f7ed41471b7bd1ae57 DeliveryCount=1 Subscription=audit
[13:23:43 INF] Processing MessageId=2e89f7622777c0f7ed41471b7bd1ae57 DeliveryCount=1 Subscription=search-index
[13:23:43 INF] Processing MessageId=283da8b31e975393dd7c5234d4312b73 DeliveryCount=1 Subscription=audit
[13:23:43 INF] Audit: recorded QuoteDeleted for quote 2001 (EventId=283da8b31e975393dd7c5234d4312b73)
```

Idempotency — the same id delivered twice, one side effect:

```text
[13:23:46 INF] Audit: recorded QuoteCreated for quote 1001 (EventId=cb1f6bae3a3fe2c7499ed44e2480d3d9)
[13:23:46 INF] Duplicate MessageId=cb1f6bae3a3fe2c7499ed44e2480d3d9 for Subscription=audit - completing without side effect
[13:23:46 INF] Duplicate MessageId=cb1f6bae3a3fe2c7499ed44e2480d3d9 for Subscription=search-index - completing without side effect
```

Those duplicates arrive at `DeliveryCount=1`: a second *message*, not a
redelivery of one — the case broker duplicate detection would have caught. The
consumer-side store covers both, which is why the guarantee belongs there.

Dead-letter — a non-JSON body, settled on the first delivery:

```text
[13:23:53 INF] Processing MessageId=poison-a92908863cf64caf9fff819e2df46e32 DeliveryCount=1 Subscription=audit
[13:23:53 WRN] Poison message detected MessageId=poison-a92908863cf64caf9fff819e2df46e32 Reason=InvalidPayload. Dead-lettering immediately.
System.Text.Json.JsonException: 't' is an invalid start of a property name.
```

The transaction — side effect, then dedupe row, then completion:

```text
INSERT INTO "QuoteAuditEntries" (...) RETURNING "Id";
INSERT INTO "ProcessedMessages" ("MessageId", "SubscriptionName", "Outcome", "ProcessedAtUtc") VALUES (...);
[13:23:43 INF] Completed MessageId=fb57f8158e3baade4c00920c812f29c0 EventType=QuoteCreated in 31ms
```

## What is not verified

- **Two API instances competing on one subscription.** The suite runs one host.
  Competing consumers *within* a subscription needs two processes.
- **`MaxDeliveryCountExceeded`.** Only the immediate poison route is asserted.
- **Graceful shutdown draining an in-flight handler.**
- **A real Azure namespace.** Everything here is the emulator, which supports AMQP
  over TCP only, hosts one non-renameable namespace (`sbemulatorns`), and reads
  its topology from a static file.
- **Unit tests for the worker's decision logic.** The classifier and the store are
  covered directly; the processor is covered only end-to-end. Tests built on
  `ServiceBusModelFactory` for the dedupe skip, abandon-vs-dead-letter and
  scope-per-message paths are the first thing to add.

## Production limitations

- In-memory nothing here, but **the app is still one deployable consuming both
  subscriptions**. A real system would split them, which is what a topic is for.
- **The publish/commit gap** above, until an outbox exists.
- **Retention.** `ProcessedMessages` grows without a cleanup job, and its window
  must exceed message TTL plus DLQ dwell time or the guarantee silently lapses
  for old messages.
- **CI does not build this.** `.github/workflows/ci.yml` targets `Day5/piece2`
  while the maintained backend has been `Day7/piece2` since Day 11 — so nothing
  in Days 11, 12, 17, 18 or 19 has been compiled by CI. Running this suite for
  the first time in weeks surfaced two failures that had nothing to do with Day
  19: a test asking for an `items` property Day 12 had renamed, and a migrations
  assertion that could not pass while startup created the SQL Server schema with
  `EnsureCreatedAsync`. Both are fixed on this branch. Pointing CI at Day 7 is
  the change that stops that recurring.
