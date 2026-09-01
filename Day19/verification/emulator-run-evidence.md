# Day 19 — evidence from the emulator run (1 September 2026, 13:23)

Lines below are copied verbatim from `dotnet test Day7/piece2/Quotes.Tests.Integration.ServiceBus`
against the Azure Service Bus emulator (Testcontainers: emulator + SQL Server on
one Docker network). Full result:

```text
Test summary: total: 5, failed: 0, succeeded: 5, skipped: 0, duration: 80.9s
```

The solution suite in the same session:

```text
Test summary: total: 198, failed: 0, succeeded: 196..198, duration: 41.1s
```

## Fan-out: one publish, two subscriptions

One message id, picked up by both workers, each on its own subscription:

```text
[13:23:43 INF] Published QuoteCreated for quote 999 with MessageId fb57f8158e3baade4c00920c812f29c0
[13:23:43 INF] Processing MessageId=fb57f8158e3baade4c00920c812f29c0 DeliveryCount=1 Subscription=audit
[13:23:43 INF] Processing MessageId=fb57f8158e3baade4c00920c812f29c0 DeliveryCount=1 Subscription=search-index
[13:23:43 INF] Audit: recorded QuoteCreated for quote 999 (EventId=fb57f8158e3baade4c00920c812f29c0)
[13:23:43 INF] SearchIndex: upserted projection for quote 999 (EventType=QuoteCreated, EventId=fb57f8158e3baade4c00920c812f29c0)
```

Two `ProcessedMessages` rows for that one publish, one per subscription — the
composite key doing its job:

```text
INSERT INTO "ProcessedMessages" ("MessageId", "SubscriptionName", "Outcome", "ProcessedAtUtc")
VALUES (@p0, @p1, @p2, @p3);      -- @p1 Size = 5   ("audit")
INSERT INTO "ProcessedMessages" ("MessageId", "SubscriptionName", "Outcome", "ProcessedAtUtc")
VALUES (@p0, @p1, @p2, @p3);      -- @p1 Size = 12  ("search-index")
```

## The filter: search-index never sees a delete

Quote 2001, created then deleted. Audit processes both; the delete never reaches
the other subscription, because the subscription's SQL filter drops it at the
broker:

```text
[13:23:43 INF] Processing MessageId=2e89f7622777c0f7ed41471b7bd1ae57 DeliveryCount=1 Subscription=audit
[13:23:43 INF] Processing MessageId=2e89f7622777c0f7ed41471b7bd1ae57 DeliveryCount=1 Subscription=search-index
[13:23:43 INF] SearchIndex: upserted projection for quote 2001 (EventType=QuoteCreated, EventId=2e89f7622777c0f7ed41471b7bd1ae57)
[13:23:43 INF] Processing MessageId=283da8b31e975393dd7c5234d4312b73 DeliveryCount=1 Subscription=audit
[13:23:43 INF] Audit: recorded QuoteDeleted for quote 2001 (EventId=283da8b31e975393dd7c5234d4312b73)
```

The delete's id appears once — on `audit` only. There is no
`Subscription=search-index` line for `283da8b3…`, and the test asserts that
absence against `ProcessedMessages` rather than inferring it from the log.

## Idempotency: the same message id delivered twice

Quote 1001, sent twice with the same `MessageId`. Broker duplicate detection is
OFF on the topic, so both copies were delivered; the dedupe store is the only
thing between that and two audit rows:

```text
[13:23:46 INF] Processing MessageId=cb1f6bae3a3fe2c7499ed44e2480d3d9 DeliveryCount=1 Subscription=audit
[13:23:46 INF] Audit: recorded QuoteCreated for quote 1001 (EventId=cb1f6bae3a3fe2c7499ed44e2480d3d9)
[13:23:46 INF] Completed MessageId=cb1f6bae3a3fe2c7499ed44e2480d3d9 EventType=QuoteCreated in 195ms

[13:23:46 INF] Processing MessageId=cb1f6bae3a3fe2c7499ed44e2480d3d9 DeliveryCount=1 Subscription=audit
[13:23:46 INF] Duplicate MessageId=cb1f6bae3a3fe2c7499ed44e2480d3d9 for Subscription=audit - completing without side effect

[13:23:46 INF] Processing MessageId=cb1f6bae3a3fe2c7499ed44e2480d3d9 DeliveryCount=1 Subscription=search-index
[13:23:46 INF] Duplicate MessageId=cb1f6bae3a3fe2c7499ed44e2480d3d9 for Subscription=search-index - completing without side effect
```

Note `DeliveryCount=1` on the duplicates. This is a genuine second *delivery* of
a second message, not a redelivery of one — which is the case broker-side
duplicate detection would have caught, and the case the consumer-side store also
covers. The test then asserts exactly one `QuoteAuditEntries` row.

## Dead-letter: poison payload, first delivery

A body that is not JSON, dead-lettered immediately by both workers rather than
retried until `MaxDeliveryCount` runs out:

```text
[13:23:53 INF] Processing MessageId=poison-a92908863cf64caf9fff819e2df46e32 DeliveryCount=1 Subscription=search-index
[13:23:53 INF] Processing MessageId=poison-a92908863cf64caf9fff819e2df46e32 DeliveryCount=1 Subscription=audit
[13:23:53 WRN] Poison message detected MessageId=poison-a92908863cf64caf9fff819e2df46e32 Reason=InvalidPayload. Dead-lettering immediately.
System.Text.Json.JsonException: 't' is an invalid start of a property name. Expected a '"'. Path: $ | LineNumber: 0 | BytePositionInLine: 2.
```

`DeliveryCount=1` is the whole point: the test asserts the message is in the
subscription's DLQ with `DeadLetterReason = InvalidPayload` after **one**
delivery. The other route — three deliveries, then
`MaxDeliveryCountExceeded` — belongs to failures that might be transient.

## The transaction

Every successful message shows the same order: side effect, then dedupe row,
then completion — inside one transaction on the scope's `DbContext`:

```text
INSERT INTO "QuoteAuditEntries" (...) RETURNING "Id";
INSERT INTO "ProcessedMessages" ("MessageId", "SubscriptionName", "Outcome", "ProcessedAtUtc") VALUES (...);
[13:23:43 INF] Completed MessageId=fb57f8158e3baade4c00920c812f29c0 EventType=QuoteCreated in 31ms
```

## Not evidenced here

- Two API instances splitting one subscription. The run has one host with one
  worker per subscription; competing consumers *within* a subscription needs two
  processes and is not covered by this suite.
- `MaxDeliveryCountExceeded` dead-lettering. Only the immediate poison route is
  asserted.
- Graceful shutdown draining an in-flight handler.
- Anything against a real Azure namespace: every line above is the emulator.
