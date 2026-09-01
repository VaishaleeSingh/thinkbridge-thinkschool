# Day 19 — review of the Service Bus implementation

Reviewed against `Day19/docs/day19-service-bus-topics-dlq-implementation-plan.md`
on 1 September 2026, on branch `day19-service-bus-topics-dlq`.

**Status, 1 September 13:23 — everything below has now been compiled and run.**
`dotnet test Day7/piece2/QuotesApi.slnx` is 198/198 and the emulator suite is
5/5; `Day19/verification/emulator-run-evidence.md` quotes the run. The paragraph
that follows was written before a toolchain was available and is kept because it
is what the review was worth at the time: the fixes below were reasoning, not
observation, until that run.

**As written, nothing here had been compiled or executed.** No .NET SDK exists
on the machine this review ran on, and the container it ran from has no network
route to install one. Every finding below is from reading the code and checking
it against Microsoft's current documentation; every fix is a source change that
has not been through a compiler. The first thing to do on a machine with the
.NET 10 SDK is `dotnet build Day7/piece2/QuotesApi.slnx`, before trusting any of
it. This is the same limitation Day 13 recorded, and it is recorded here for the
same reason.

What could be checked mechanically is checked, and captured beside this file:

- `verify-day19.py` / `verify-day19-output.txt` — 25 file-level assertions,
  all passing.
- `idempotency-proof.py` / `idempotency-proof-output.txt` — the defect in
  finding 1 and its fix, executed against real SQLite using the migration's
  schema. Two competing consumers under the old two-transaction shape leave
  **2** audit rows for one message; under the fix, **1**. A crash between the
  old code's two commits repeats the work on redelivery; under the fix it does
  not. Five scenarios, all behaving as claimed.
- `day19-evidence-runbook.md` — the exact commands for the evidence that needs
  the SDK and Docker, and where each screenshot goes.

None of it is a test suite, and none of it can tell you the code compiles.
`idempotency-proof.py` models the transaction boundaries; it does not execute
`QuoteEventProcessorService`.

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

## Found by actually running the suite (1 September)

The build failed first on an XML comment of mine: `--` is illegal inside an XML
comment, and MSBuild rejects the project file at restore (MSB4025), so one
comment in `Quotes.Tests.Integration.ServiceBus.csproj` looked like a broken
solution. Fixed, and `verify-day19.py` now scans every project file for it.

With that gone the solution built and 196 of 198 tests passed. Both failures
were in the SQL Server suite and **neither came from Day 19** — all four files
involved are byte-identical to `main`:

### 13. `TwoConcurrentRequests_AddingSameQuoteId_ResultInExactlyOneItem` — broken since Day 12

The test reads `finalState.GetProperty("items")`. Day 12 (`288d2b6`) replaced
the collection read shape with `CollectionDetail`, whose list is named
`Quotes`. The test has not been touched since Day 7 (`b8e5cc9`), so it has been
asking for a property that does not exist, and failing with a bare
`KeyNotFoundException` that names neither the rename nor the test's real
subject. **Fixed** on this branch: `quotes`, with a comment saying why.

### 14. `Factory_OnStartup_AppliesAllMigrationsToFreshSqlServerDatabase` — broken since 24 August

`Program.cs` branched on `IsSqlServer()` and called `EnsureCreatedAsync`
(commit `20ac73a`, 24 August). `EnsureCreated` writes no
`__EFMigrationsHistory`, so `GetAppliedMigrationsAsync()` returns empty and the
assertion that every migration is applied cannot pass. Worse than the red test:
the SQL Server migrations project was bypassed entirely, so it drifted from the
model for a fortnight, and a deployed database created that way cannot be moved
forward at all — only dropped.

**Fixed** on this branch, per your decision: `Program.cs` is back to one
`MigrateAsync` path for both providers. This requires the SQL Server migration
set to be regenerated first — one `dotnet ef migrations add`, step 0 of the
runbook — which cannot be run from this environment. A database created by the
old path also needs baselining before the next deploy; the runbook has the
script.

### Why neither was noticed

`.github/workflows/ci.yml` restores, builds and tests `Day5/piece2/QuotesApi.slnx`.
The maintained backend has been `Day7/piece2` since Day 11. Nothing in Days 11,
12, 17, 18 or 19 has ever been compiled or tested by CI, and this suite had not
run anywhere in weeks. Pointing CI at Day 7 is the change that stops this
recurring — flagged rather than made, since it affects the whole repository.

## Second pass — three things that were narrated rather than true

### 15. The search-index handler was unreachable production code

`SearchIndexQuoteEventHandler` was registered as a keyed service, but the app
ran exactly one worker, on `audit`. Nothing ever resolved the `search-index`
key, so the handler could not execute in production; the only thing touching
that subscription was a test receiving from it directly. A comment explained
this as deliberate, which made it a decision on paper and dead code in fact.

**Fixed:** the worker now takes its subscription name as a constructor argument
and is registered once per subscription, each instance owning its own
`ServiceBusProcessor`. Both subscriptions are consumed in-process, which is
what the topic was for. Handlers are keyed by the *configured* names rather
than string literals, so renaming a subscription cannot leave the lookup
pointing at nothing. `IQuoteEventHandler.SubscriptionName` is gone with it — the
keyed registration is now the single place that association lives.

Because the app consumes `search-index` itself, the emulator test that received
from that subscription directly would now be competing with it for the same
messages. Filtering is asserted through `ProcessedMessages` instead — one row
per (message, subscription) — so "search-index never saw the delete" is a
database fact rather than an inference. A new test asserts the composite key's
whole point: one publish, two rows, one per subscription.

### 16. A setting that read like a knob and turned nothing

`ServiceBusOptions.MaxDeliveryCount` was never read by any code. It is a
property of the *subscription*, set in the Bicep, and only the broker acts on
it — so changing the app setting looked meaningful and did nothing. **Removed**
from the options and from `appsettings.json`, with a comment where it was
saying where the real value lives. `verify-day19.py` now fails if any
`ServiceBus` option goes unread.

### 17. Two copies of the emulator topology

`Day19/verification/emulator/config.json` was a copy of the test project's
`emulator-config.json`. **Fixed:** the compose file mounts the test project's
file by relative path and the copy is deleted. One topology, one file — a copy
drifts, and the copy that drifts is always the one nobody runs.

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
5. **No manual evidence yet.** Every screenshot the plan asks for — the two
   subscriptions' counts diverging, two instances splitting the work, a DLQ with
   both reasons in it, shutdown draining in-flight handlers — requires a running
   emulator or namespace, which requires a machine with Docker and the .NET SDK.
   `Day19/verification/screenshots/` is still empty and should not be presented
   as anything else. `day19-evidence-runbook.md` has the commands, and
   `verification/emulator/docker-compose.yml` starts a broker that outlives a
   test run so the DLQ can actually be photographed.
