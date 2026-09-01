# Day 19 — review of the Service Bus implementation

Reviewed against `Day19/docs/day19-service-bus-topics-dlq-implementation-plan.md`
on 1 September 2026, on branch `day19-service-bus-topics-dlq`.

**Read this first: nothing here was compiled or executed.** No .NET SDK exists
on the machine this review ran on, and the container it ran from has no network
route to install one. Every finding below is from reading the code and checking
it against Microsoft's current documentation; every fix is a source change that
has not been through a compiler. The first thing to do on a machine with the
.NET 10 SDK is `dotnet build Day7/piece2/QuotesApi.slnx`, before trusting any of
it. This is the same limitation Day 13 recorded, and it is recorded here for the
same reason.

`Day19/verification/verify-day19.py` is what could be checked mechanically —
25 file-level assertions, all passing (`verify-day19-output.txt`). It is not a
test suite. It cannot tell you the code compiles.

## Defects found and fixed

### 1. The idempotency guarantee did not hold (correctness — the central defect)

`QuoteEventProcessorService` called the handler, which committed its own side
effect with its own `SaveChangesAsync`, and only then called
`store.RecordAsync`, which committed again. Two transactions.

Consequences, both of them the exact failure the exercise exists to prevent:

- a crash between the two commits leaves the audit row written with nothing
  recording that the message was handled, so the redelivery writes it again;
- under `MaxConcurrentCalls = 4`, two consumers both pass the `HasSeenAsync`
  pre-check, both write an audit row, and the loser of the dedupe INSERT race
  had its *duplicate side effect already committed*. The `catch` swallowed the
  unique violation and completed the message, so the only thing discarded was
  the dedupe row — precisely backwards.

The code carried a comment saying the record happened "inside the handler's same
UoW". Sharing a `DbContext` is not sharing a transaction.

**Fixed:** the processor now opens one transaction on the scope's `DbContext`,
runs the handler, writes the dedupe row, and commits. On a unique violation it
rolls back — discarding the duplicate side effect — and completes the message.

### 2. Unique-constraint detection matched on exception text

`IsUniqueConstraintViolation` searched the inner exception's message for
`"UNIQUE constraint failed"`, `"duplicate key"`, and the substrings `"2627"` and
`"2601"`. Message text is localised and changes between provider versions, and a
substring search for `2627` matches any message that happens to contain those
four digits. An idempotency guarantee resting on string matching is not a
guarantee.

**Fixed:** typed on the provider exception — `SqliteException.SqliteErrorCode == 19`
(`SQLITE_CONSTRAINT`) and `SqlException.Number is 2627 or 2601`.

### 3. The projection's primary key was database-generated

`QuoteSearchProjection.QuoteId` is the id of the quote the event describes, but
EF's convention for an integer key made it `ValueGeneratedOnAdd` — visible in the
migration as `.Annotation("Sqlite:Autoincrement", true)`. On SQL Server that is
an IDENTITY column, and the first upsert fails with *"Cannot insert explicit
value for identity column"*. SQLite would have accepted it, so this would have
passed locally and in the SQLite integration suite and failed only in Azure and
in the SQL Server Testcontainers suite.

**Fixed:** `ValueGeneratedNever()` in `QuotesDbContext`, with the annotation
removed from the migration and both model snapshots kept in step.

### 4. The one end-to-end emulator test could never pass

It published an event and then waited for a row in `QuoteSearchProjections`. The
application runs exactly one processor, on the **audit** subscription; nothing
in the app consumes `search-index`. The test would have polled for 15 seconds
and failed every run. A test that cannot pass is worse than no test, because in
a list of test names it reads as coverage.

**Fixed:** rewritten as four tests that assert what the system actually does —
round-trip into `QuoteAuditEntries`; the same `MessageId` sent twice producing
one audit row; the `search-index` filter dropping `QuoteDeleted`, checked by
receiving from that subscription directly; and a malformed payload arriving in
the DLQ with `DeadLetterReason = InvalidPayload` on delivery 1, which is the
distinction between the two dead-letter routes made into an assertion.

### 5. The emulator fixture could not have started

Three independent problems, all confirmed against Microsoft's emulator
documentation:

- **Networking.** `SQL_SERVER` was set to `_sqlServer.Hostname` — the *host-side*
  name, `localhost`. The emulator resolves that from inside its own container,
  where it points at itself. The SQL container declared a network alias but no
  network was ever created or attached, so the alias did nothing. The emulator
  requires SQL Server reachable by hostname on a shared Docker network.
- **Namespace name.** The config used `emulatorNamespace`. The emulator hosts
  exactly one namespace and its preset name cannot be renamed: it must be
  `sbemulatorns`.
- **Rule schema.** Rules were written as `"FilterType": "SqlFilter"` with
  `SqlExpression` inline. The emulator's schema is `"FilterType": "Sql"` with a
  nested `"SqlFilter": { "SqlExpression": ... }` object. The `Logging` section
  the shipped sample includes was missing.

**Fixed:** an explicit `NetworkBuilder` network with both containers on it and
`SQL_SERVER` set to the SQL container's alias; namespace renamed to
`sbemulatorns`; rule schema corrected; `Logging` added.

### 6. The tests asked for a transport the emulator does not support

`ServiceBusTransportType.AmqpWebSockets` was set explicitly on the test client.
The emulator supports AMQP over TCP only; WebSockets is documented as
unsupported. **Fixed:** transport left at its default.

### 7. Startup validation would have rejected the emulator host

The test host set `ServiceBus:Enabled = true` but no `FullyQualifiedNamespace`.
`RequiredIfEnabled` plus `ValidateOnStart` means the host refuses to boot — the
suite would have failed before reaching a single assertion. **Fixed:** the test
host now sets it (the client is replaced afterwards, but the option still has to
validate).

### 8. The Service Bus test project was not in the solution

`QuotesApi.slnx` did not reference `Quotes.Tests.Integration.ServiceBus`, so
nothing compiled it — not a solution build, not a solution test run, not CI. The
csproj comment presented this as deliberate and suggested
`dotnet test QuotesApi.slnx filter "Category=..."`, which is not valid syntax
(and no such category exists). An uncompiled test project rots silently, which
is a worse failure than an honest red test on a machine without Docker.

**Fixed:** added to the solution, matching how `Quotes.Tests.Integration.SqlServer`
is already carried, with the real skip command in the comment.

### 9. A post-commit publish rode the request's cancellation token

All three write endpoints published with the request `CancellationToken`. The
database write has already committed at that point, so a client that disconnects
— or a browser that cancels — also cancels the publish, and the event describing
a durable change is dropped. The catch-all in the publisher logs it as an error
and the request has already returned.

**Fixed:** `CancellationToken.None` for the publish, with the reason in a comment
at each call site.

### 10. The dead-letter diagnostics route leaked exception detail and opened its own connection

It returned `Results.Problem($"Error peeking DLQ: {ex.Message}")` — a broker
exception message can carry entity paths, namespace names and token-acquisition
detail, and the repository's rule since Day 18 is that exception text does not
cross an HTTP boundary. It also constructed a new `ServiceBusClient` per request,
against the singleton rule stated three files away.

**Fixed:** resolves the registered singleton client, logs the exception, returns
a bare `502` with no detail.

### 11. Unrelated comments deleted from `QuoteEndpointExtensions`

The Day 19 edit removed two explanatory comment blocks from the POST handler
(the `sub` / `NameIdentifier` claim explanation, and why `Quote.Create` re-checks
the endpoint's validation) that had nothing to do with messaging. **Restored.**

### 12. Smaller corrections

- `QuoteEventProcessorService.StopAsync` disposed the `ServiceBusProcessor`,
  which the DI container owns as a singleton and disposes itself — a
  double-dispose of a shared object. Now stops without disposing.
- `ServiceBusOptions.MaxDeliveryCount` was `[Range(1, 10)]`, an arbitrary local
  cap; Service Bus accepts up to 2000. More to the point the value is
  informational — the broker owns the real setting, in the Bicep — which the
  XML doc now says.
- The Bicep comment claimed the `$Default` rule was "removed". ARM has no delete
  verb for a rule; it is *overwritten* with `1=0`. Rules are OR'd, so the
  effective filter is the SQL rule alone — the outcome was right, the
  explanation was not.
- The Bicep claimed `MaxDeliveryCount = 3` reaches the DLQ "in ~3 minutes
  (3 × LockDuration)". That holds only when a consumer dies holding the lock; an
  explicit `AbandonMessageAsync` redelivers immediately. Reworded.

## Verified against Microsoft's documentation

| Claim | Verdict |
|---|---|
| Basic tier has queues only; topics need Standard or Premium | Correct — the pricing tier table lists Topics as unavailable in Basic (de-duplication too) |
| Default `MaxDeliveryCount` is 10 | Correct |
| `DeadLetterReason` values: `MaxDeliveryCountExceeded`, `TTLExpiredException`, `HeaderSizeExceeded`, `MaxTransferHopCountExceeded`, session-id null | Correct |
| DLQ path `<topic>/Subscriptions/<sub>/$deadletterqueue`, addressed in the SDK by `SubQueue.DeadLetter` | Correct — the implementation uses the SDK option, not a hand-built path |
| RBAC can be assigned at topic-subscription scope | Correct — supported, though the portal cannot do it (CLI or ARM only). The Bicep's per-subscription Receiver assignments are valid |
| Emulator needs a SQL container, hosts one non-renameable namespace, AMQP TCP only, no runtime config reload | Correct — and three of those were being violated, see finding 5 |

## Open gaps — not fixed, and why

1. **No unit tests for the processor.** The plan asked for the decision logic to
   be tested with `ServiceBusModelFactory`-built messages: dedupe skip, abandon
   vs dead-letter, scope-per-message, cancellation. Those tests do not exist,
   and the three unit-test files that do exist cover the event record, the
   classifier and the store — none of them exercise a line of
   `QuoteEventProcessorService`. Writing them blind, against
   `ProcessMessageEventArgs` and a substituted `ServiceBusReceiver`, without a
   compiler to check a single signature, would produce plausible-looking code of
   unknown validity — which is exactly what this review was called in to find.
   This is the first thing to write on a machine with the SDK.
2. **`QuoteEventPublisherTests` tests the event, not the publisher.** Nothing
   asserts that the published message carries `MessageId`, `eventType`,
   `traceparent`, or that a send failure does not throw. Same reason.
3. **CI does not build any of this.** `.github/workflows/ci.yml` restores,
   builds and tests `Day5/piece2/QuotesApi.slnx`. The maintained backend has
   been `Day7/piece2` since Day 11. Nothing in Day 11, 12, 17, 18 or 19 has ever
   been compiled by CI. This is a repository-wide gap rather than a Day 19 one,
   and it is why "CI is green" currently says nothing about this branch — but it
   does mean adding the ServiceBus project to the solution (finding 8) buys
   nothing until CI points at Day 7.
4. **The publish/commit gap is still open by design.** Publishing after
   `SaveChangesAsync` is not atomic; the plan says so, the code says so in a
   comment, and the outbox is described rather than built. Unchanged — but it
   should be stated in the submission rather than left to a reader to notice.
5. **No manual evidence.** Every screenshot the plan asks for — the two
   subscriptions' counts diverging, two instances splitting the work, a DLQ with
   both reasons in it, shutdown draining in-flight handlers — requires a running
   emulator or namespace, which requires a machine with Docker and the .NET SDK.
   `Day19/verification/screenshots/` is still empty and should not be presented
   as anything else.
