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
# Each worker now creates its own processor (one per subscription), so it owns
# and disposes it. Stop must come first: StopProcessingAsync is what lets
# in-flight handlers finish inside the shutdown timeout.
check(
    "worker stops the processor it owns, then disposes it",
    "StopProcessingAsync" in proc
    and "processor.DisposeAsync()" in proc
    and proc.index("StopProcessingAsync") < proc.index("processor.DisposeAsync()"),
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
# The host moved into the fixture: one host, one database, one set of
# consumers for the whole collection. Per-test hosts all consumed the same
# subscriptions, so a message published by one test could be handled by
# another test's worker and written to a database the assertion never reads.
check(
    "the emulator host lives in the collection fixture, not per test",
    "public WebApplicationFactory<Program> Factory" in fixture
    and "WebApplicationFactory<Program>" not in sb_tests,
)
check(
    "emulator host sets FullyQualifiedNamespace so ValidateOnStart passes",
    'UseSetting("ServiceBus:FullyQualifiedNamespace"' in fixture,
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

# --- 9. Both subscriptions are actually consumed ------------------------
messaging = read("QuotesApi/Extensions/MessagingExtensions.cs")
check(
    "one worker registered per subscription, both from configuration",
    messaging.count("CreateWorker(sp, opts.TopicName!") == 2
    and "opts.AuditSubscription!" in messaging
    and "opts.SearchIndexSubscription!" in messaging,
    "a handler registered but never consumed is unreachable production code",
)
check(
    "handlers are keyed by the configured subscription names, not literals",
    'AddKeyedScoped<IQuoteEventHandler, AuditQuoteEventHandler>(\n            opts.AuditSubscription!)' in messaging
    and 'AddKeyedScoped<IQuoteEventHandler, SearchIndexQuoteEventHandler>(\n            opts.SearchIndexSubscription!)' in messaging,
)

# --- 10. No settings that read like knobs and turn nothing --------------
options_src = read("QuotesApi/Messaging/ServiceBusOptions.cs")
declared = set(re.findall(r"public\s+[\w?<>]+\s+(\w+)\s*{\s*get;\s*set;", options_src))
declared.discard("Enabled")
used_anywhere = ""
for rel in (
    "QuotesApi/Extensions/MessagingExtensions.cs",
    "QuotesApi/Messaging/QuoteEventProcessorService.cs",
    "QuotesApi/Messaging/ServiceBusQuoteEventPublisher.cs",
    "QuotesApi/Extensions/DiagnosticsEndpointExtensions.cs",
):
    used_anywhere += read(rel)

unread = sorted(name for name in declared if name not in used_anywhere)
check(
    "every ServiceBus option is read by something",
    not unread,
    ", ".join(unread) or "MaxDeliveryCount belongs to the subscription, not the app",
)

# --- 11. Braces balance in every file this branch touches ---------------
# CS1513 is what an edit that inserts a method in the wrong place produces,
# and it is cheap to catch here: strip comments and string literals, then
# count. Not a parser, but it would have caught the one that got through.
def brace_delta(text):
    text = re.sub(r"//[^\n]*", "", text)
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    text = re.sub(r'"(?:[^"\\\n]|\\.)*"', '""', text)
    text = re.sub(r"'(?:[^'\\\n]|\\.)*'", "''", text)
    return text.count("{") - text.count("}")


unbalanced = []
for dirpath, dirnames, filenames in os.walk(API):
    dirnames[:] = [d for d in dirnames if d not in ("bin", "obj", ".git")]
    for filename in filenames:
        if not filename.endswith(".cs"):
            continue
        path = os.path.join(dirpath, filename)
        with io.open(path, encoding="utf-8-sig") as fh:
            if brace_delta(fh.read()):
                unbalanced.append(os.path.relpath(path, ROOT))

check(
    "braces balance in every C# file",
    not unbalanced,
    ", ".join(unbalanced) or "catches the CS1513 an ill-placed insert produces",
)

# --- 12. XML comments are well-formed -----------------------------------
# MSBuild refuses to load a project whose comment contains "--" (MSB4025),
# and it fails at RESTORE, before any code is compiled -- so one stray
# double hyphen in a comment looks like a broken build, not a typo.
xml_offenders = []
for base in (API, os.path.join(ROOT, "Day19")):
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj", ".git")]
        for filename in filenames:
            if not filename.endswith((".csproj", ".slnx", ".props", ".targets", ".runsettings")):
                continue
            path = os.path.join(dirpath, filename)
            with io.open(path, encoding="utf-8-sig") as fh:
                text = fh.read()
            for match in re.finditer(r"<!--(.*?)-->", text, re.S):
                body = match.group(1)
                if "--" in body or body.endswith("-"):
                    xml_offenders.append(os.path.relpath(path, ROOT))

check(
    "no double hyphen inside an XML comment in any project file",
    not xml_offenders,
    ", ".join(sorted(set(xml_offenders))) or "MSB4025 fails restore before it compiles anything",
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
