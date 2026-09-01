#!/usr/bin/env python3
"""
Day 19 static verification.

This is NOT a substitute for `dotnet build` / `dotnet test`. No .NET SDK was
available on the machine this review ran on (see the submission notes), so
these checks assert the things that can be asserted without a compiler:
file-level facts that a reviewer would otherwise have to take on trust.

Run from the repository root:  python3 Day19/verification/verify-day19.py
Exit code 0 = every check passed.
"""
import io
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
API = os.path.join(ROOT, "Day7", "piece2")

results = []


def read(rel):
    with io.open(os.path.join(API, rel), encoding="utf-8-sig") as fh:
        return fh.read()


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))


# --- 1. Idempotency is transactional -----------------------------------
proc = read("QuotesApi/Messaging/QuoteEventProcessorService.cs")
check(
    "processor opens one transaction around handler + dedupe row",
    "BeginTransactionAsync" in proc
    and proc.index("BeginTransactionAsync") < proc.index("await handler.HandleAsync")
    and proc.index("await handler.HandleAsync") < proc.index("await store.RecordAsync")
    and proc.index("await store.RecordAsync") < proc.index("transaction.CommitAsync"),
    "order: begin -> handle -> record -> commit",
)
check(
    "duplicate race rolls the side effect back",
    "transaction.RollbackAsync" in proc,
)
check(
    "unique-violation detection uses provider error codes, not message text",
    "SqliteErrorCode" in proc and "sql.Number is 2627 or 2601" in proc
    and "UNIQUE constraint failed" not in proc,
)
check(
    "worker stops the processor without disposing a DI-owned singleton",
    "StopProcessingAsync" in proc and "processor.DisposeAsync()" not in proc,
)
check(
    "explicit settlement on every path",
    all(s in proc for s in ("CompleteMessageAsync", "AbandonMessageAsync", "DeadLetterMessageAsync")),
)

# --- 2. Projection key is not database-generated ------------------------
ctx = read("QuotesApi/Data/QuotesDbContext.cs")
check(
    "QuoteSearchProjection.QuoteId is ValueGeneratedNever",
    "entity.Property(x => x.QuoteId).ValueGeneratedNever();" in ctx,
    "otherwise SQL Server makes it IDENTITY and the first upsert fails",
)
mig = read("QuotesApi/Migrations/20260901050401_AddMessagingTables.cs")
projection_block = mig[mig.index('name: "QuoteSearchProjections"'):]
check(
    "migration does not mark the projection key autoincrement",
    "Sqlite:Autoincrement" not in projection_block[: projection_block.index("constraints:")],
)
for snap in (
    "QuotesApi/Migrations/QuotesDbContextModelSnapshot.cs",
    "QuotesApi/Migrations/20260901050401_AddMessagingTables.Designer.cs",
):
    text = read(snap)
    block = text[text.index('Entity("QuotesApi.Models.QuoteSearchProjection"'):]
    block = block[: block.index("ToTable")]
    check(f"{os.path.basename(snap)} agrees with the model", "ValueGeneratedOnAdd" not in block)

# --- 3. Composite dedupe key -------------------------------------------
check(
    "ProcessedMessages primary key is (MessageId, SubscriptionName)",
    "HasKey(x => new { x.MessageId, x.SubscriptionName })" in ctx,
)

# --- 4. Publishing ------------------------------------------------------
endpoints = read("QuotesApi/Extensions/QuoteEndpointExtensions.cs")
check(
    "post-commit publish does not ride the request cancellation token",
    endpoints.count("PublishAsync(evt, CancellationToken.None)") == 3
    and "PublishAsync(evt, cancellationToken)" not in endpoints,
)
publisher = read("QuotesApi/Messaging/ServiceBusQuoteEventPublisher.cs")
check("MessageId is the deterministic event id", "MessageId = evt.EventId" in publisher)
check(
    "eventType travels as an application property (filters cannot read the body)",
    'ApplicationProperties["eventType"]' in publisher,
)
check("trace context travels as a string", 'ApplicationProperties["traceparent"]' in publisher)

# --- 5. No secrets, no connection strings -------------------------------
settings = read("QuotesApi/appsettings.json")
check(
    "no Service Bus connection string in configuration",
    "SharedAccessKey" not in settings and "Endpoint=sb://" not in settings,
)

# --- 6. Emulator setup matches what the emulator actually requires ------
cfg_path = os.path.join(API, "Quotes.Tests.Integration.ServiceBus", "emulator-config.json")
with io.open(cfg_path, encoding="utf-8") as fh:
    cfg = json.load(fh)
ns = cfg["UserConfig"]["Namespaces"][0]
check("emulator namespace is the non-renameable 'sbemulatorns'", ns["Name"] == "sbemulatorns")
check("emulator config declares a Logging section", "Logging" in cfg["UserConfig"])
rules = {
    sub["Name"]: sub.get("Rules", [])
    for sub in ns["Topics"][0]["Subscriptions"]
}
check(
    "search-index rule uses the emulator's Sql/SqlFilter schema",
    rules["search-index"][0]["Properties"]["FilterType"] == "Sql"
    and "SqlExpression" in rules["search-index"][0]["Properties"]["SqlFilter"],
)
check(
    "search-index filter excludes deletes",
    "QuoteDeleted" not in rules["search-index"][0]["Properties"]["SqlFilter"]["SqlExpression"],
)
fixture = read("Quotes.Tests.Integration.ServiceBus/ServiceBusEmulatorFixture.cs")
check(
    "both containers share a Docker network and SQL is addressed by alias",
    "NetworkBuilder" in fixture
    and "WithNetwork(_network)" in fixture
    and 'WithEnvironment("SQL_SERVER", SqlAlias)' in fixture,
)
sb_tests = read("Quotes.Tests.Integration.ServiceBus/EmulatorIntegrationTests.cs")
# Matches the code, not the prose: both files explain in comments WHY
# WebSockets is not used, so a bare substring search would fail on its own
# documentation.
check(
    "tests do not request AMQP WebSockets (unsupported by the emulator)",
    "ServiceBusTransportType.AmqpWebSockets" not in sb_tests
    and "ServiceBusTransportType.AmqpWebSockets" not in fixture,
)
check(
    "round-trip test asserts on the subscription the app actually consumes",
    "QuoteAuditEntries" in sb_tests,
)
check(
    "emulator host sets FullyQualifiedNamespace so ValidateOnStart passes",
    'UseSetting("ServiceBus:FullyQualifiedNamespace"' in sb_tests,
)

# --- 7. The Service Bus suite is actually compiled ----------------------
slnx = read("QuotesApi.slnx")
check(
    "Service Bus test project is in the solution",
    "Quotes.Tests.Integration.ServiceBus.csproj" in slnx,
    "a project outside the solution is never built by CI",
)

# --- 8. Nothing left disabled by default --------------------------------
check(
    'ServiceBus is off unless configured ("Enabled": false)',
    re.search(r'"ServiceBus"\s*:\s*{\s*"Enabled"\s*:\s*false', settings) is not None,
)

width = max(len(n) for n, _, _ in results)
failed = 0
for name, ok, detail in results:
    status = "PASS" if ok else "FAIL"
    if not ok:
        failed += 1
    line = f"[{status}] {name.ljust(width)}"
    if detail:
        line += f"   ({detail})"
    print(line)

print()
print(f"{len(results) - failed}/{len(results)} checks passed")
sys.exit(1 if failed else 0)
