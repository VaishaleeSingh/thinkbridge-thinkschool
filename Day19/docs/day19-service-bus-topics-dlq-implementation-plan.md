# Day 19 — Azure Service Bus topics, competing consumers, idempotency and the DLQ

## Detailed task prompt

Our API currently tells nobody when something happens to a quote. Day 18 moved
slow work off the request thread, but that queue lives inside one process: it is
lost on restart, it is invisible to any other service, and a second replica has
its own copy of it. Day 19 replaces that boundary with a real broker.

Publish a domain event to an **Azure Service Bus topic** whenever a quote is
created, updated or deleted. Give the topic **two subscriptions** that want
different slices of the same stream — one that audits every event, one that only
cares about content changes — so that fan-out is demonstrated by configuration
(a subscription filter) rather than by publishing the same message twice.

Consume one of those subscriptions with a **competing-consumer worker**: a hosted
service running a `ServiceBusProcessor` with more than one concurrent call, such
that two instances of the API drain the same subscription and neither processes
the other's message. Because the broker guarantees **at-least-once** delivery, and
because the competing consumers make redelivery ordinary rather than exceptional,
every handler must be **idempotent**: dedupe on a message id, persisted, so that
the same event delivered twice produces one side effect and the second delivery is
completed without repeating the work.

Finally, demonstrate the **dead-letter queue**. Send a message the handler cannot
ever process — a poison message — and show it landing in the subscription's DLQ:
once by exhausting `MaxDeliveryCount` through repeated transient failures, and once
by dead-lettering it immediately and deliberately with a reason and description,
because a payload that will never parse should not be retried ten times first.
Read the dead-lettered message back and show the reason it carries.

Explain the choices in practical language: why a topic rather than a queue, why
`PeekLock` rather than `ReceiveAndDelete`, why the broker's own duplicate
detection is not sufficient on its own, when to abandon versus dead-letter, and
what has to happen operationally so a DLQ is not simply a place where messages go
to be forgotten.

## Goal

Move the "something happened to a quote" signal out of the process and onto a
broker, with delivery semantics stated honestly rather than assumed:

- one publisher, one topic, two subscriptions with different filters;
- one competing-consumer worker with bounded concurrency and cooperative shutdown;
- handlers that are safe to run twice, proven by a test that runs them twice;
- a dead-letter path that is entered deliberately, observable, and drainable.

Day 18's in-memory channel stays where it is. Day 19 is the answer to the
limitation Day 18 wrote down in its own risks section ("accepted work is lost on
restart", "multiple replicas have isolated queues") — it should reference that
limitation and show what changes, not quietly delete it.

## Repository analysis and implementation boundary

The maintained backend is `Day7/piece2/QuotesApi`. Days 11, 12 and 18 add focused
backend lessons to that project in place, keeping only documentation and evidence
in their own day folders. Day 19 follows the same convention:

- production code goes in `Day7/piece2/QuotesApi`;
- tests go in the existing `Quotes.Tests.Unit` and `Quotes.Tests.Integration`
  projects, plus one new integration project only if an emulator-gated suite
  needs its own `Testcontainers` fixture (see "Local development" below);
- the plan, submission notes, commands and evidence stay in `Day19/`.

Do not copy the API into `Day19/piece2`. That produces another stale fork and
makes the messaging diff unreviewable.

Relevant existing seams the implementation should reuse rather than reinvent:

| Need | Existing seam |
|---|---|
| Options binding + fail-fast validation | `AddOptions<T>().Bind(...).ValidateDataAnnotations().ValidateOnStart()` in `Extensions/InfrastructureExtensions.cs` |
| Hosted worker with cooperative shutdown | `BackgroundJobs/QueuedBackgroundJobService.cs` (Day 18) and the configured `HostOptions.ShutdownTimeout` |
| Endpoint mapping | `Extensions/*EndpointExtensions.cs`, mapped from `Program.cs` |
| Persistence and migrations | `Data/QuotesDbContext.cs`, `Migrations/` (SQLite locally, SQL Server in Azure and the Testcontainers suite) |
| Tracing | `Extensions/ObservabilityExtensions.cs` — add the Service Bus source there, do not start a second telemetry stack |
| Correlation | `Middleware/CorrelationIdMiddleware.cs` — the trace id crosses the broker as a message property, not as an object |
| Credentials | `DefaultAzureCredential` + Key Vault, already wired in `Program.cs` |

## Proposed use case

`QuoteChanged` events, published from the write paths that already exist in
`Extensions/QuoteEndpointExtensions.cs`.

1. `POST/PUT/DELETE /api/quotes/...` succeeds and commits.
2. The endpoint publishes one `QuoteChangedEvent` to topic `quote-events`, with
   an application property `eventType` of `QuoteCreated`, `QuoteUpdated` or
   `QuoteDeleted`, and `MessageId` set to a deterministic event id.
3. Subscription **`audit`** takes everything (`1=1`, the default `TrueFilter`)
   and its handler appends an audit row.
4. Subscription **`search-index`** takes only
   `eventType IN ('QuoteCreated','QuoteUpdated')` via a SQL filter, and its
   handler upserts a projection row. Deletes are intentionally out of its scope,
   which is what makes the filter observable: publish three events, one
   subscription sees three, the other sees two.
5. A competing-consumer worker drains `audit` with `MaxConcurrentCalls > 1`.
6. A deliberately poisoned event lands in `audit/$DeadLetterQueue`.

This is repository-relevant because quote writes already exist, already carry an
owner id and a trace id, and produce a side effect worth deduping. It changes no
existing response shape.

### A note on the publish step, stated rather than hidden

Publishing after `SaveChangesAsync` is **not** atomic with the database write. The
process can commit and then fail before the send, and the event is lost; or send
and then fail to record that it sent. The honest options are:

- **accept it** for this exercise, publish after commit, and document the gap;
- **transactional outbox** — write the event to an `OutboxMessages` table inside
  the same transaction, and let a relay publish and mark it sent.

The implementation should take the first option in code and describe the second
in the submission, with the one-paragraph version of why the outbox is the
standard answer and what it costs (a relay, ordering care, and at-least-once
publishing that the consumer's idempotency already tolerates). Claiming
"exactly once" anywhere in the documentation is a factual error and should be
caught in review.

## Architecture

### 1. Topology, and who owns it

Entities: topic `quote-events`; subscriptions `audit` and `search-index`.

- **Declare the topology in infrastructure, not in application startup.** Add
  Bicep under `Day19/infra/` (or extend the existing `azd` infrastructure if the
  repository's Day 5 `azd` setup is being carried forward) for the namespace,
  topic, subscriptions and rules. An app that creates its own topics needs
  `Manage` rights in production, which is precisely the right it should not have.
- The app gets **`Azure Service Bus Data Sender`** on the topic and
  **`Azure Service Bus Data Receiver`** on the subscription, via managed identity
  — no connection strings in configuration, matching how Key Vault is already
  wired in `Program.cs`.
- For local development a `dotnet run`-time bootstrap guarded by
  `IsDevelopment()` may create missing entities against the emulator only. It
  must be impossible to reach in a deployed environment.

Settings to pin explicitly, because their defaults are the source of most
surprises:

| Setting | Value | Why |
|---|---|---|
| `MaxDeliveryCount` (per subscription) | `3` | The default of 10 makes the DLQ demo tediously slow and hides poison messages behind a minute of retries |
| `LockDuration` | `PT1M` | Long enough for the handler, short enough that a crashed consumer's message returns quickly |
| `DefaultMessageTimeToLive` | bounded (e.g. `P7D`) | An unbounded TTL turns an unread subscription into an unbounded bill |
| `DeadLetteringOnMessageExpiration` | `true` on `audit` | An expired audit event should be inspectable, not silently dropped |
| `RequiresDuplicateDetection` | `false` | Deliberate — see §4 |
| `EnableBatchedOperations` | `true` | Defaults are fine, but state it so the reader knows it was considered |

Subscription rules: `search-index` must have its default `$Default` `TrueFilter`
**removed** when the SQL filter is added. Adding a rule does not replace the
default one, and a subscription with both matches everything — this is the single
most common "my filter does nothing" bug and the plan should call it out so the
implementation does not rediscover it.

### 2. Publisher

`IQuoteEventPublisher` with one implementation over `ServiceBusSender`.

- Register `ServiceBusClient` as a **singleton** (`AddAzureClients` from
  `Microsoft.Extensions.Azure`, or a plain singleton registration). It owns an
  AMQP connection; creating one per request is the classic Service Bus
  performance bug, and the SDK's clients are thread-safe by design.
- `ServiceBusSender` is likewise long-lived, resolved once per topic.
- Every message carries:
  - `MessageId` = a deterministic event id (see §4), **not** `Guid.NewGuid()` at
    send time, because a publish retried by the SDK's own retry policy must not
    become two distinct ids;
  - `Subject` / `ApplicationProperties["eventType"]` — the value the subscription
    filter matches on. Filtering on the body is not possible; only system
    properties and application properties are addressable in a SQL filter, and
    getting this wrong is the second most common filter bug;
  - `ApplicationProperties["traceparent"]` — the current `Activity` id as a
    **string**. Never place an `Activity`, a `ClaimsPrincipal`, an
    `HttpContext`, a bearer token or a `DbContext` in a message. Day 18's rule
    carries over unchanged and matters more here, because the payload now leaves
    the process;
  - `CorrelationId` = the request trace id, so a support question can be answered
    across the boundary;
  - `ContentType` = `application/json`, and a `schemaVersion` property, so the
    consumer can reject a shape it does not understand rather than guessing.
- The body is a small immutable record serialised with `System.Text.Json` — quote
  id, owner id, occurred-at, and the fields a consumer actually needs. Not the EF
  entity: serialising an entity ships navigation properties, lazy-loading
  surprises and internal columns across a boundary that is now a public contract.
- Send failures must not fail the HTTP request that already committed. Log at
  error with the event id and let the request return its normal response; the
  submission documents this as the gap the outbox would close.

### 3. Competing-consumer worker

`QuoteEventProcessorService : BackgroundService`, wrapping `ServiceBusProcessor`.

- Create the processor with `ServiceBusProcessorOptions`:
  - `ReceiveMode = PeekLock` (the default, but stated): `ReceiveAndDelete` loses
    the message on any handler failure, and makes both the retry story and the
    DLQ story impossible;
  - `MaxConcurrentCalls` from validated options, default `4` — this is what makes
    a single instance a competing consumer over its own subscription, and two
    instances competing consumers over the broker's;
  - `AutoCompleteMessages = false`. This is the decision the whole exercise turns
    on. With auto-complete, a message is completed when the handler returns
    without throwing, which means an idempotency check that swallows a duplicate
    and a handler that quietly did nothing are indistinguishable. Completing
    explicitly makes every outcome — complete, abandon, dead-letter — a line of
    code someone chose;
  - `PrefetchCount` left at `0` initially. Prefetch interacts with `LockDuration`:
    prefetched messages are locked while they sit in the client's buffer, so an
    aggressive prefetch with a slow handler produces lock expiry and redelivery.
    Tune it only with a number and a reason.
- `MaxAutoLockRenewalDuration` set to comfortably exceed the slowest expected
  handler; otherwise a long handler finishes work on a message whose lock has
  expired, `CompleteMessageAsync` throws `ServiceBusException` with
  `MessageLockLost`, and the message is redelivered — the exact scenario the
  idempotency store exists to survive, and worth demonstrating rather than only
  describing.
- `ProcessErrorAsync` is not optional: it is the only place SDK-level faults
  (link failures, credential expiry, entity-not-found) surface. Log with
  `args.EntityPath`, `args.ErrorSource` and `args.FullyQualifiedNamespace`; do not
  throw from it.
- Shutdown: `StopProcessingAsync(stoppingToken)` in `StopAsync`/on cancellation
  lets in-flight handlers finish within the host's shutdown timeout instead of
  killing them mid-transaction. The `HostOptions.ShutdownTimeout` Day 18 already
  configures is the bound; reuse it rather than adding a second one.
- Each message gets its **own async DI scope** (`IServiceScopeFactory.CreateAsyncScope()`),
  exactly as Day 18's worker does, because the handler resolves a scoped
  `QuotesDbContext`.

### 4. Idempotency — dedupe on a message id

Two mechanisms exist and they are not interchangeable. The plan should implement
one and explain why the other is not enough.

**Broker-side duplicate detection.** Setting `RequiresDuplicateDetection` on the
topic makes Service Bus discard a second message with the same `MessageId` inside
a configurable window (20 seconds to 7 days, 10 minutes by default). It protects
against a *publisher* sending twice. It does **not** protect the consumer against
redelivery, because redelivery after a lock expiry, an abandon, or a consumer
crash is not a duplicate send — the broker is correctly delivering one message
again. This is why the topic is created with duplicate detection **off** and the
guarantee is implemented where it actually holds.

**Consumer-side dedupe (the one being built).** A `ProcessedMessages` table:

```text
ProcessedMessages
  MessageId        TEXT/NVARCHAR(128)  PRIMARY KEY
  SubscriptionName TEXT                PRIMARY KEY (composite with MessageId)
  ProcessedAtUtc   datetime
  Outcome          TEXT
```

The key is composite on purpose. Two subscriptions receive the *same* message id
from the same publish, and they are different pieces of work; a single-column key
would let the `audit` handler's row suppress the `search-index` handler's work.

The handler contract:

1. Begin a transaction on the scoped `QuotesDbContext`.
2. Apply the side effect.
3. Insert the `ProcessedMessages` row.
4. Commit.
5. `CompleteMessageAsync`.

The insert must be inside the same transaction as the side effect. A check-then-act
(`if (await store.HasSeen(id)) return;`) is a race under `MaxConcurrentCalls > 1`
and across replicas: two consumers can both read "not seen" and both do the work.
The primary key is the actual guarantee — the second writer gets a unique-constraint
violation, the handler catches exactly that (`DbUpdateException` with the provider's
unique-violation code), treats it as "already processed", and completes the message.
A cheap pre-check is fine as an optimisation but must not be mistaken for the
guarantee.

Note that step 4 and step 5 are still not atomic: a crash between commit and
complete redelivers a message whose work is done — which the dedupe row then
absorbs on the next attempt. That is the intended behaviour and should be called
out as the reason the store exists rather than treated as a leftover flaw.

`MessageId` generation: a deterministic value the publisher can reproduce, e.g. a
GUIDv5-style hash over `(eventType, quoteId, version-or-occurredAtTicks)`, or a
`Guid` allocated once when the event object is constructed and reused across
publish retries. Whichever is chosen, it must not be re-generated inside a retry.

Retention: terminal rows grow forever otherwise. Add a bounded retention (a
configured window, plus an index on `ProcessedAtUtc`) and a cleanup path, and say
in the documentation that the retention window must exceed the maximum plausible
redelivery delay — including a message's TTL and any DLQ replay — or the dedupe
guarantee silently lapses for old messages.

### 5. Dead-lettering — both routes, deliberately

The exercise asks for a poison message. There are two ways in, and the difference
between them is the interesting part:

**a. Exhausting `MaxDeliveryCount`.** The handler throws (or calls
`AbandonMessageAsync`) on every attempt. With `MaxDeliveryCount = 3` the message
is delivered three times and the broker moves it to
`quote-events/Subscriptions/audit/$DeadLetterQueue` with
`DeadLetterReason = MaxDeliveryCountExceeded`. This is the right path for a
failure that *might* be transient — a database timeout, a downstream 503.

**b. Explicit `DeadLetterMessageAsync(reason, description)`.** The handler detects
a failure that repetition cannot fix — malformed JSON, an unknown `schemaVersion`,
a quote id that does not parse — and dead-letters immediately with a reason such
as `InvalidPayload` and a description carrying enough detail to triage, and no
personal data or token material in it (`DeadLetterErrorDescription` is readable by
anyone with receive rights on the DLQ). Retrying a message that can never succeed
burns the delivery budget, delays every message behind it, and produces three
identical error logs where one would do.

Implement both. The classification rule — "is this exception retryable?" — should
be one small, unit-testable function, not an inline `catch` in the handler.

The DLQ is a queue, not an alert. The plan must include:

- a **`GET /api/admin/quote-events/dead-letters`** diagnostic endpoint (or a small
  CLI script) that peeks the DLQ and returns id, reason, description and enqueued
  time — never the raw body if it may contain user content, and behind
  authorization, following the existing `DiagnosticsEndpointExtensions.cs`
  precedent of routes that do not exist outside Development unless explicitly
  enabled;
- a note that the real operational answer is an **alert on
  `DeadletteredMessages > 0`** for the subscription (Azure Monitor metric), because
  a DLQ nobody looks at is indistinguishable from data loss;
- a described-not-necessarily-built **replay** path: receive from the DLQ, fix or
  re-publish to the topic, complete the dead-lettered copy. Replay re-enters the
  idempotent handler, which is exactly why the dedupe store must outlive the DLQ
  dwell time.

### 6. Configuration

```jsonc
"ServiceBus": {
  "FullyQualifiedNamespace": "quotes-<env>.servicebus.windows.net", // no connection string
  "TopicName": "quote-events",
  "AuditSubscription": "audit",
  "SearchIndexSubscription": "search-index",
  "MaxConcurrentCalls": 4,
  "PrefetchCount": 0,
  "MaxAutoLockRenewalMinutes": 5,
  "MaxDeliveryCount": 3,
  "Enabled": false // off unless configured, like the OTLP exporter already is
}
```

Bound with `ValidateDataAnnotations().ValidateOnStart()`, following `JwtOptions`.
`Enabled: false` by default matters: the existing integration suite boots the real
`Program.cs` in `WebApplicationFactory`, and a processor that tries to open an AMQP
connection at startup would fail every unrelated test on a machine with no
namespace. The same pattern `ObservabilityExtensions` uses for the OTLP endpoint
(wire it up only when configured) applies here, and the publisher falls back to a
no-op implementation when disabled.

## Local development

Prefer the **Azure Service Bus emulator** (container image, driven by a
`config.json` that declares topics, subscriptions and rules) so the exercise is
runnable without a subscription, and so CI can run the integration suite.
Constraints to verify before committing to it, and to record honestly in the
submission if any of them bite:

- the emulator requires a companion SQL Edge container;
- it is configured by a static file — entities cannot be created at runtime;
- feature coverage is not identical to the cloud service; anything the emulator
  cannot do must be verified against a real namespace and labelled as such.

If the emulator proves unworkable in this environment, fall back to a real
namespace on the **Basic**-tier-is-not-enough note: **topics and subscriptions
require Standard or Premium**; the Basic tier has queues only. Record which was
used, and mark emulator-only or cloud-only results explicitly.

Follow the repository's existing pattern for container-dependent tests: a
`Testcontainers` fixture and a collection that skips (not fails) when Docker is
unavailable, as `Quotes.Tests.Integration.SqlServer` already does.

## Planned file changes

```text
Day7/piece2/QuotesApi/
  Messaging/
    ServiceBusOptions.cs
    QuoteChangedEvent.cs                 # immutable contract record + schemaVersion
    IQuoteEventPublisher.cs
    ServiceBusQuoteEventPublisher.cs
    NoOpQuoteEventPublisher.cs           # used when ServiceBus:Enabled is false
    QuoteEventProcessorService.cs        # BackgroundService + ServiceBusProcessor
    IQuoteEventHandler.cs
    AuditQuoteEventHandler.cs
    SearchIndexQuoteEventHandler.cs
    MessageFailureClassifier.cs          # retryable vs poison
    IProcessedMessageStore.cs
    EfProcessedMessageStore.cs
  Data/
    QuotesDbContext.cs                   # + ProcessedMessages, + QuoteAuditEntries
  Models/
    ProcessedMessage.cs
    QuoteAuditEntry.cs
  Migrations/
    <timestamp>_AddMessagingTables.cs
  Extensions/
    MessagingExtensions.cs               # client, sender, processor, options, handlers
    InfrastructureExtensions.cs          # call AddMessaging(...)
    QuoteEndpointExtensions.cs           # publish after successful write
    DiagnosticsEndpointExtensions.cs     # dead-letter peek route (Development-gated)
    ObservabilityExtensions.cs           # add the Azure.Messaging.ServiceBus ActivitySource
  Program.cs
  appsettings.json
  QuotesApi.csproj                       # Azure.Messaging.ServiceBus, Microsoft.Extensions.Azure

Day7/piece2/Quotes.Tests.Unit/
  Messaging/
    QuoteEventPublisherTests.cs
    QuoteEventProcessorServiceTests.cs   # ServiceBusModelFactory-built messages
    ProcessedMessageStoreTests.cs
    MessageFailureClassifierTests.cs

Day7/piece2/Quotes.Tests.Integration/
  QuoteEventPublishingTests.cs           # write endpoints publish; disabled => no-op

Day7/piece2/Quotes.Tests.Integration.ServiceBus/   # emulator-gated, skips without Docker
  ServiceBusEmulatorFixture.cs
  TopicFanOutTests.cs
  CompetingConsumerTests.cs
  DeadLetterTests.cs

Day19/
  docs/
    day19-service-bus-topics-dlq-implementation-plan.md   # this file
    day19-service-bus-topics-dlq-submission.md            # added during implementation
  infra/
    servicebus.bicep
  verification/
    screenshots/
```

Names may consolidate where a type stays trivial, but publisher, processor,
handlers, dedupe store and failure classification stay separate concerns —
the classifier and the store are the two pieces most worth testing alone.

## Implementation sequence

1. Add the topology (Bicep + emulator `config.json`) and the options type with
   startup validation; prove the app still boots with messaging disabled.
2. Add `QuoteChangedEvent`, the publisher interface, the no-op implementation and
   the registration switch. Assert in an integration test that a quote write
   still succeeds and publishes nothing when disabled.
3. Implement `ServiceBusQuoteEventPublisher`; publish from the three write
   endpoints. Verify with the emulator that three events land on `audit` and two
   on `search-index`, and delete the `$Default` rule so the filter is real.
4. Add the `ProcessedMessages` and audit/projection tables plus one migration;
   run it against SQLite and against the SQL Server Testcontainers suite.
5. Implement `EfProcessedMessageStore` with the composite key and the
   unique-violation-as-duplicate path; unit-test the concurrent case directly.
6. Implement handlers and `MessageFailureClassifier`; unit-test both branches.
7. Implement `QuoteEventProcessorService` with `AutoCompleteMessages = false`,
   explicit complete/abandon/dead-letter, `ProcessErrorAsync`, and
   `StopProcessingAsync` on shutdown.
8. Emulator tests: fan-out, competing consumers with two processor instances,
   redelivery producing one side effect, `MaxDeliveryCount` DLQ, explicit DLQ.
9. Add the dead-letter peek route and the trace-context propagation.
10. Manual verification and evidence capture; write the submission document,
    including everything that could not be verified and why.

## Test strategy

### Unit tests (no broker)

`ServiceBusModelFactory.ServiceBusReceivedMessage(...)` builds a
`ServiceBusReceivedMessage` with a chosen `MessageId`, `DeliveryCount`, body and
application properties — this is what makes the processor's decision logic
testable without a namespace. Cover:

- publisher sets `MessageId`, `Subject`, `ContentType`, `CorrelationId` and
  `traceparent`, and the body contains no entity/navigation data;
- the same event published twice yields the same `MessageId`;
- a message whose id is already in the store is completed **without** the side
  effect running (assert on the fake handler, not on a log line);
- a transient failure abandons; a poison failure dead-letters with a reason;
- classification: `DbUpdateConcurrencyException` and a timeout are retryable,
  `JsonException` and an unknown `schemaVersion` are not;
- cancellation during handling stops processing and does not complete the message;
- every message resolves its handler from a fresh scope, and the scope is disposed
  after failure and after cancellation.

No `Task.Delay`-based assertions — use `TaskCompletionSource` as Day 18 does.

### Integration tests (emulator, skipped without Docker)

- three writes → `audit` sees 3, `search-index` sees 2 (the delete is filtered);
- two processor instances against one subscription each handle a disjoint subset,
  and the union is the whole set — the competing-consumer proof;
- a handler that abandons twice then succeeds produces exactly one audit row —
  the redelivery/idempotency proof;
- a handler that always throws produces a message in the DLQ with
  `DeadLetterReason = MaxDeliveryCountExceeded` after exactly `MaxDeliveryCount`
  deliveries;
- a malformed payload is dead-lettered on the **first** delivery with reason
  `InvalidPayload`;
- the DLQ receiver reads both back and the peek endpoint reports them.

### Manual verification (evidence for the submission)

1. Publish create/update/delete and show the two subscriptions' message counts
   diverging (portal or `az servicebus` / Service Bus Explorer).
2. Run two API instances; show each worker's logs handling different message ids.
3. Force a redelivery (kill an instance mid-handler, or let a lock expire) and
   show one audit row and a "duplicate, completing" log line for the second.
4. Show the DLQ after the poison message, including `DeadLetterReason` and
   `DeadLetterErrorDescription`, for both dead-letter routes.
5. Stop the host during processing and show in-flight handlers finishing inside
   the shutdown timeout rather than being cut off.
6. Show the Azure Monitor `DeadletteredMessages` metric (or state that it was not
   verified, if the emulator was used throughout).

Screenshots land in `Day19/verification/screenshots/` with the same numbered
naming Day 18 used.

## Observability

- Add `Azure.Messaging.ServiceBus` to the traced sources in
  `ObservabilityExtensions.cs`; the SDK emits producer and consumer spans and will
  link them when the trace context travels as a message property.
- Propagate `traceparent` explicitly as an application property and restore it in
  the handler by starting an `Activity` with that parent id, so a consumer span is
  a child of the request that published it rather than an orphan.
- Structured log fields on every path: `MessageId`, `SubscriptionName`,
  `EventType`, `DeliveryCount`, `Outcome`, elapsed ms. Never log the token, the
  `Authorization` header, or the full body.
- Metrics worth having if they cost nothing extra: active message count, DLQ
  count, handler duration. Logs plus DLQ contents are the minimum acceptance
  evidence.

## Risks and mitigations

- **At-least-once mistaken for exactly-once** — the dedupe store, and documentation
  that says so plainly.
- **Filter that does nothing** — the `$Default` `TrueFilter` must be removed; the
  fan-out test asserts unequal counts, so a broken filter fails a test rather than
  passing silently.
- **Check-then-act dedupe race** under concurrency — unique constraint as the
  guarantee, catch the violation.
- **Lock expiry mid-handler** — `MaxAutoLockRenewalDuration`, and a handler that
  survives redelivery anyway.
- **Poison message retried ten times** — `MaxDeliveryCount = 3` plus explicit
  dead-lettering for non-retryable failures.
- **DLQ as a black hole** — peek endpoint, alert on the metric, documented replay.
- **Publish/commit gap** — acknowledged, with the outbox described as the fix.
- **Connection churn** — singleton `ServiceBusClient`/`ServiceBusSender`.
- **Unrelated tests failing without a namespace** — `Enabled: false` by default and
  a no-op publisher.
- **Secrets in configuration** — fully-qualified namespace plus
  `DefaultAzureCredential`; no connection string in the repository, matching the
  Key Vault pattern already in `Program.cs`.
- **Dedupe table growth** — bounded retention with a window longer than TTL plus
  DLQ dwell time.

## Acceptance criteria

- A topic with two subscriptions exists as declared infrastructure, and the two
  subscriptions demonstrably receive different subsets of one published stream.
- Quote create/update/delete publish one message each, carrying a deterministic
  `MessageId`, an `eventType` property, and trace context — and no request-scoped
  or secret material.
- A `BackgroundService` consumes a subscription with `MaxConcurrentCalls > 1` and
  `AutoCompleteMessages = false`, completing, abandoning or dead-lettering every
  message explicitly.
- Two instances against one subscription split the work; neither double-processes.
- Redelivering a message produces exactly one side effect, proven by a test that
  delivers the same message id twice.
- A poison message reaches the subscription DLQ by both routes, with reasons that
  distinguish them, and can be read back.
- Shutdown lets in-flight handlers finish within the configured timeout and starts
  no new work.
- Tests cover publishing, filtering, competing consumption, idempotency,
  classification and dead-lettering, without wall-clock sleeps, and the suite still
  passes with messaging disabled.
- The submission documents at-least-once semantics, why broker duplicate detection
  is not the guarantee, abandon vs dead-letter, the publish/commit gap and the
  outbox, DLQ operations, and everything that could not be verified in this
  environment.
