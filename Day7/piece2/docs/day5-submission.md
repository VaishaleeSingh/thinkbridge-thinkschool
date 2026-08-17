# Day 5 — mentor submission

## GitHub link

https://github.com/thinkbridge-thinkschool/VaishaleeSingh/tree/day5-diagnose-slow-endpoint/Day5/piece2

(Replace with the pull request URL once opened — the diff and the CI run are
both reachable from there.)

## Notes for mentor

Fix commit: `05ce794`. Reproduction commit: `2f720bb`.
Full write-up and trace screenshots: `Day5/piece2/docs/slow-endpoint-diagnosis.md`.

### Diagnosis note

The trace for `GET /api/collections` came back with 32 spans: the request span,
one `SELECT` over `Collections`, and thirty near-identical `SELECT`s against
`Quotes`, one per collection returned. No single query is slow — each runs in
roughly 7–12 ms against local SQLite — but thirty of them in sequence is 524 ms
of the 524 ms request. `ListByOwnerAsync` loaded the caller's collections, then
looped over them fetching each one's quotes in its own round trip, so query
count grew linearly with rows: a textbook N+1. Against a networked database
each round trip also carries latency, so this degrades exactly as customers add
collections. The fix gathers every quote id first and fetches them in one
query.

### Measured

Same owner (`seed-user`), same machine, SQLite, local Jaeger via OTLP.

| | Collections | Spans | DB spans | Response time |
|---|---|---|---|---|
| Before | 30 | 32 | 31 | 523.78 ms (495–811 ms across five runs) |
| After  | 60 | 3  | 2  | 33.8 ms (33.8–144.3 ms across five runs) |

Trace `30f48a3` before, `12a0ddc` after. The two rows are deliberately not on
equal data, and the asymmetry runs the safe way: the seeding script uses a
fixed `sub` and the SQLite file is never reset, so the *after* row is answering
for twice as many collections in 2 queries instead of 31 — roughly 15× faster
on double the rows. Had the difference gone the other way the comparison would
be worthless.

Screenshots in `docs/images/`: before timeline, before span detail showing each
child span is its own `SELECT` against `Quotes`, after timeline, after span
detail showing one `SELECT` with every quote id in a single `IN` clause, and an
11 ms single-query trace for reference.

### Why two queries and not one

`CollectionItem` is an owned type (`OwnsMany`), so `Collections` already arrives
with its items joined in — but an item holds only a `QuoteId`, and `Quote` is a
separate aggregate. Folding the quote lookup into the collections query would
join across an aggregate boundary and fan the collection rows out by their item
count. Two constant queries is the honest shape. What matters is that the count
no longer depends on N.

### Keeping it fixed

`CollectionListingQueryCountTests` installs an EF `DbCommandInterceptor`, lists
collections for an owner with 3 and again with 15, and asserts the command
count is the same number both times.

Deliberately not an exact count — that breaks the moment someone legitimately
adds a lookup — and deliberately not a duration, which would be flaky. It uses
SQLite rather than the InMemory provider because InMemory is not relational,
never issues a `DbCommand`, and a command interceptor against it would silently
count zero and pass forever.

## What did you learn this session?

That an N+1 is invisible in the metric everyone watches. Every behavioural test
in this repository passed against the broken version, because the endpoint
returned exactly the right data — it just asked 31 times. The trace did not
find it by duration either; it found it by *shape*, a staircase of identical
spans differing only in a parameter. And the proof is not one screenshot: it is
that the span count moves when you re-seed with a different number of
collections. That relationship is the bug.

## What would break this?

- **Pagination — there is none.** A user with 10,000 collections now costs two
  queries instead of 10,001, but still loads every row into memory. The N+1 is
  fixed; the unbounded result set is not.
- **The `IN` list is unbounded.** On SQL Server this eventually meets the
  2,100-parameter limit. EF Core 8+ switches to `OPENJSON` and saves it, but
  the code is relying on a provider detail it never states.
- **Quotes deleted out from under a collection are silently skipped**, matching
  the old behaviour. That is a swallowed inconsistency, not a decision anyone
  made explicitly.
- **The regression test pins relative count, not absolute.** Someone could make
  this five constant queries and it would still pass.
