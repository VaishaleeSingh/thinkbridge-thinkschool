# Day 20 — The outbox pattern

## Task prompt

> A DB write and a queue publish must not diverge. Implement the transactional
> outbox: write the domain change + an outbox row in one EF transaction, then a
> relay publishes and marks sent. Prove no message is lost if the publish step
> crashes.

**What this builds:** Outbox / transactional messaging · EF Core relationships

## Exercise

> Paste the outbox table + relay. Describe the crash scenario you tested and why
> no message is lost or duplicated (at-least-once + idempotent consumer).

## Where the answer is

| Document | What it is |
|---|---|
| `day20-transactional-outbox-exercise.md` | The exercise, answered in the four parts it asks for |
| `../scripts/verify-crash-recovery.ps1` | The crash proof, runnable: it starts the API, asserts the event is committed and unpublished, force-kills the process, restarts, and checks the message was delivered |
| `../verification/day20-crash-recovery-run.txt` | Console transcripts of both runs, the migration DDL, and the relay's claim SQL |
| `../verification/screenshots/` | Terminal captures of the crash proof and the suite |

The implementation itself is in `Day7/piece2` — code, migrations for both
providers, and the tests named in the exercise answer.

## Starting point

Day 19 published to Service Bus from inside the request handler, after the
database write had already committed, and caught every exception so the caller
still got its 201. `ServiceBusQuoteEventPublisher` said so in its own comment:

```
// PUBLISH/COMMIT GAP: the database write already committed.
"Failed to publish {EventType} for quote {QuoteId} (EventId={EventId}). " +
"The database write succeeded; this event is lost unless replayed from an outbox."
```

There was no outbox to replay from. That sentence is what Day 20 answers.
