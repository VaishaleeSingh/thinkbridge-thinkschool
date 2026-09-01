# Day 19 — evidence runbook

Two kinds of evidence exist for this branch. The first kind is already
captured, in this folder. The second kind needs a machine with the .NET 10 SDK
and Docker, and this runbook is how to produce it in about twenty minutes.

## Already captured (runs anywhere with Python)

| Artifact | What it establishes |
|---|---|
| `verify-day19.py` → `verify-day19-output.txt` | 25 file-level assertions: the transaction is in the right place, the projection key is not database-generated, the emulator config matches what the emulator requires, no connection string is committed, the test project is in the solution. 25/25. |
| `idempotency-proof.py` → `idempotency-proof-output.txt` | The defect and the fix, executed against real SQLite using the migration's schema. Two competing consumers with the old two-transaction shape leave **2** audit rows for one message; with the fix, **1**. A crash between commits repeats the work; with the fix it does not. 5/5. |
| `day19-review-findings.md` | The twelve defects, why each is wrong, and what changed. |

What none of them establish: **that the C# compiles.** No .NET SDK was
available where this branch was reviewed, and no package feed was reachable to
install one. `idempotency-proof.py` models the transaction boundaries; it does
not execute `QuoteEventProcessorService`.

## Still to capture — needs the SDK and Docker

Run these on your own machine, from the repository root, on this branch.

### 0. Regenerate the SQL Server migrations (one command, do this first)

The SQL Server migration set stops at `20260812114103_InitialCreate`. Since 24
August, `Program.cs` created the SQL Server schema with `EnsureCreatedAsync`
instead, which records nothing in `__EFMigrationsHistory` and bypassed that
project entirely, so it drifted further from the model with every day that
added a column. Day 19 restores a single `MigrateAsync` path for both
providers, which means the migration set has to catch up first:

```bash
dotnet tool install --global dotnet-ef      # once, if you do not have it
cd Day7/piece2
dotnet ef migrations add SyncModelThroughDay19 \
  --project QuotesApi.Migrations.SqlServer \
  --startup-project QuotesApi.Migrations.SqlServer \
  --context QuotesApi.Data.QuotesDbContext \
  --output-dir Migrations
```

This needs no Docker and no live SQL Server: `migrations add` only diffs the
model against the last migration. One migration captures everything since 12
August, including Day 19's `ProcessedMessages`, `QuoteAuditEntries` and
`QuoteSearchProjections`.

Then `SqlServerMigrationTests` means something again: it asserts that every
migration in the assembly is applied to a fresh database, which is exactly the
guarantee `EnsureCreated` had quietly removed.

**Deployed databases need one extra step.** Any database created by the old
`EnsureCreated` path has the tables but no `__EFMigrationsHistory`, so
`MigrateAsync` will try to `CREATE TABLE` over them and fail on the next
deploy. For this training app the simplest answer is to drop and recreate the
Azure database. If that is not acceptable, baseline it instead — list the
migration ids and mark them all as applied, since the schema already matches
the model:

```bash
dotnet ef migrations list --project QuotesApi.Migrations.SqlServer \
  --startup-project QuotesApi.Migrations.SqlServer \
  --context QuotesApi.Data.QuotesDbContext
```

```sql
IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId]    nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32)  NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;

-- One row per id from `migrations list`. ALL of them: the schema already
-- matches the model, so a partially baselined database would send Migrate off
-- to create tables that are already there.
INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260812114103_InitialCreate', N'10.0.10'),
       (N'<the id migrations list prints for SyncModelThroughDay19>', N'10.0.10');
```

### 1. It compiles (do this first — everything below assumes it)

```bash
dotnet build Day7/piece2/QuotesApi.slnx
```

Nothing else in this runbook is meaningful until this is clean. If it is not,
the errors are mine and are worth sending back before going further.

### 2. Unit and in-process integration tests

```bash
dotnet test Day7/piece2/QuotesApi.slnx --filter "FullyQualifiedName!~ServiceBus"
```

Expect green, including `QuoteEventPublishingTests` — quote writes must still
succeed with `ServiceBus:Enabled = false` and the no-op publisher, which is the
guarantee that Day 19 did not break Days 1–18.

The SQL Server suite needs Docker and step 0's migration. Its first real run in
weeks (1 September) surfaced two failures that had nothing to do with Day 19,
both fixed on this branch:

- `TwoConcurrentRequests_AddingSameQuoteId_ResultInExactlyOneItem` asked the
  response for an `items` property. Day 12 renamed that read shape's list to
  `Quotes`; the test had not been touched since Day 7. Broken since Day 12.
- `Factory_OnStartup_AppliesAllMigrationsToFreshSqlServerDatabase` asserted
  migrations were applied while startup was calling `EnsureCreatedAsync`.
  Broken since 24 August, and the reason for step 0.

Neither was caught because `.github/workflows/ci.yml` builds `Day5/piece2`,
while the maintained backend has been `Day7/piece2` since Day 11. Pointing CI
at Day 7 is the change that stops this recurring; it is a repository-wide
decision rather than a Day 19 one, so it is raised here rather than made.

Save the console output as `verification/dotnet-test-output.txt`.

### 3. The emulator suite

```bash
docker info                                          # must be running
dotnet test Day7/piece2/Quotes.Tests.Integration.ServiceBus
```

Four tests, each proving one thing the exercise asks for:

| Test | Proves |
|---|---|
| `Published_event_is_consumed_and_audited` | the worker drains the audit subscription and the handler's side effect lands |
| `Same_message_delivered_twice_produces_one_audit_row` | idempotency, with duplicate detection off at the broker so the dedupe store is the only thing standing there |
| `Search_index_subscription_filters_out_delete_events` | the SQL filter — this is the test that fails loudly if `$Default` is ever left in place |
| `Malformed_payload_is_dead_lettered_on_first_delivery` | the explicit dead-letter route, `DeadLetterReason = InvalidPayload` at `DeliveryCount = 1` |

Screenshot the run as `screenshots/01-emulator-suite-green.png`.

### 4. The manual exercise (this is what the screenshots are for)

Start a broker that outlives a test run:

```bash
docker compose -f Day19/verification/emulator/docker-compose.yml up -d
curl -s http://localhost:5300/health          # expect 200
```

Point the API at it and run it:

```bash
cd Day7/piece2
dotnet user-secrets --project QuotesApi set "Jwt:Secret" "<at least 32 characters>"
ServiceBus__Enabled=true \
ServiceBus__FullyQualifiedNamespace=localhost \
dotnet run --project QuotesApi
```

Then capture, in this order:

1. **`02-fanout-two-subscriptions.png`** — create, update and delete a quote
   through `/api/quotes`, then show the two subscriptions' message counts:
   `audit` saw three events, `search-index` saw two. The delete is the one the
   filter dropped. Service Bus Explorer, or a receiver script, either is fine.
2. **`03-competing-consumers.png`** — start a second instance on another port
   (`ASPNETCORE_URLS=http://localhost:5099`), publish a handful of events, and
   show both consoles' `Processing MessageId=…` lines with **disjoint** message
   ids. That disjointness is the competing-consumer property; two consoles
   showing the same id would mean the lock is not doing its job.
3. **`04-idempotency-redelivery.png`** — kill one instance mid-handler (or let a
   lock expire), then show the redelivery producing
   `Duplicate MessageId=… — completing without side effect` and a single audit
   row in the database.
4. **`05-dlq-both-routes.png`** — send a malformed body (immediate
   `InvalidPayload`) and a message the handler always fails on (three
   deliveries, then `MaxDeliveryCountExceeded`), then
   `GET /api/diagnostics/quote-events/dead-letters` and show both entries with
   their different reasons. The point of the screenshot is the two reasons side
   by side, not that the DLQ has messages in it.
5. **`06-graceful-shutdown.png`** — Ctrl-C while a handler is running; show the
   in-flight handler finishing and the host exiting inside its shutdown timeout,
   not being cut off.

Tear down:

```bash
docker compose -f Day19/verification/emulator/docker-compose.yml down -v
```

### 5. The tests that do not exist yet

Before the submission is honest, `QuoteEventProcessorServiceTests` needs
writing — the processor's decision logic is currently untested end to end. Use
`ServiceBusModelFactory.ServiceBusReceivedMessage(...)` to build a received
message with a chosen `MessageId` and `DeliveryCount`, and NSubstitute (already
referenced by `Quotes.Tests.Unit`) for the receiver. Cover: an already-recorded
message completes without the handler running; a transient failure abandons; a
poison failure dead-letters with a reason; cancellation neither completes nor
abandons; every message gets a fresh scope. These were left unwritten on
purpose rather than written blind against an API no compiler here could check.

## Where the evidence goes

Screenshots in `Day19/verification/screenshots/`, numbered as above, matching
the convention Day 18 used. Console captures as `.txt` beside them. Then the
submission document can cite files rather than assert outcomes.
