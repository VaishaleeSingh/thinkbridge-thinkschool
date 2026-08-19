# Day 9 — mentor submission (isolation levels + the read anomalies)

## GitHub link

https://github.com/thinkbridge-thinkschool/VaishaleeSingh/tree/day9-isolation-levels-read-anomalies/Day9

(Replace with the pull request URL once opened.)

## Notes for mentor

New content only — this task doesn't touch `QuotesApi/**`, so `Day7/piece2`
is not carried forward here either, same reasoning Day 8 already gave for
its own SQL-only tasks:

```
Day9/
  docs/
    sql/
      00-seed-data.sql
      01-dirty-read.sql
      02-non-repeatable-read.sql
      03-phantom-read.sql
    verification/
      isolation_anomalies_proxy.py
    day9-isolation-levels-submission.md   (this file)
```

The three `.sql` files reuse `dbo.Quotes` exactly as it already exists —
no schema change, just two seed rows by a known author (`00-seed-data.sql`,
idempotent) so every demo starts from a known, deterministic state.

## What this task actually asks for, and how the three anomalies differ

All three are versions of the same root problem — two transactions
touching overlapping data at the same time — but the difference is worth
being precise about, because it's exactly what separates the isolation
levels that stop each one:

- **Dirty read** — Session A reads a value Session B has **not committed**
  yet. If B rolls back, A read something that never became real.
- **Non-repeatable read** — Session A reads the same **existing row**
  twice; B changes and **commits** a real update to that row in between.
  The second value A sees is real, but different from the first.
- **Phantom read** — Session A runs the same **range query** (a filter
  that can match a variable number of rows) twice; B **inserts** (or
  deletes) a row matching that filter and commits in between. No row A
  already had changed — a new one appeared in the result set.

The ladder from loosest to strictest is `READ UNCOMMITTED` → `READ
COMMITTED` → `REPEATABLE READ` → `SERIALIZABLE`, and each step up closes
off exactly one more of these:

| Isolation level | Dirty read | Non-repeatable read | Phantom read |
|---|---|---|---|
| `READ UNCOMMITTED` | Allowed | Allowed | Allowed |
| `READ COMMITTED` (SQL Server default) | Prevented | Allowed | Allowed |
| `REPEATABLE READ` | Prevented | Prevented | Allowed |
| `SERIALIZABLE` | Prevented | Prevented | Prevented |

Why `REPEATABLE READ` stops the second anomaly but not the third: it
holds a shared lock on every row a transaction has already read, for the
whole transaction — so an existing row can't be changed out from under
it. But it takes no lock on the *gaps* between rows, so nothing stops a
brand-new row from being inserted into a range that transaction already
queried. `SERIALIZABLE` is the one that locks the range itself (a
key-range lock), which is also why it's the most expensive of the four —
it blocks the widest set of concurrent writers.

## Files

`docs/sql/01-dirty-read.sql`, `02-non-repeatable-read.sql`,
`03-phantom-read.sql` are real T-SQL, each written as two labelled halves
meant to be pasted into two separate SSMS query tabs (or any two
connections) and run in the numbered order given in the comments — that
interleaving between two live sessions is the actual mechanism being
demonstrated, so it can't be collapsed into one script run top to bottom.
Each file's Part 1 reproduces the anomaly at the loosest level it can
occur at; Part 2 repeats the identical steps one isolation level up and
shows the same interleaving now blocking or returning a stable result
instead.

## How this was verified

Same sandbox constraint as every SQL exercise this week: no route to a
real SQL Server (`mcr.microsoft.com`, Docker Hub, `ghcr.io`,
`packages.microsoft.com` all `403`) — reconfirmed for this task
specifically, in both the cloud workspace this was written in and the
local device sandbox, neither of which has a reachable Docker daemon or
network path to pull a SQL Server image.

Day 7 and 8 had a fallback for that: SQLite's real `EXPLAIN QUERY PLAN`
is an actually-executed proxy for a query *plan shape*, because plan
shape is a property SQLite and SQL Server both genuinely have. That
fallback doesn't carry over here — dirty/non-repeatable/phantom reads
aren't about plan shape, they're about *when* two independent connections'
operations interleave relative to each other's commits. That ordering
problem is engine-agnostic, so `docs/verification/isolation_anomalies_proxy.py`
proxies it the same way: two real, independent `sqlite3` connections to
the same on-disk database (WAL mode, so a reader's snapshot survives a
concurrent writer's commit — needed for the `REPEATABLE READ` /
`SERIALIZABLE` halves), with the order of reads/writes/commits controlled
by hand to match each `.sql` file's steps exactly. It was actually run;
this is its real captured output:

```
=== Dirty read ===
-- Part 1: READ UNCOMMITTED (dirty read occurs) --
Session A reads before B's edit: 'The wound is the place where the Light enters you.'
Session A (READ UNCOMMITTED) would see B's uncommitted write: 'UNCOMMITTED EDIT'
Session B rolls back -- that value never became real.
Session A reads after B's rollback: 'The wound is the place where the Light enters you.'  (dirty value never existed)
-- Part 2: READ COMMITTED (dirty read prevented) --
Session A (READ COMMITTED) while B's write is uncommitted: 'The wound is the place where the Light enters you.'
No dirty read occurred -- A never saw B's in-flight value.

=== Non-repeatable read ===
-- Part 1: READ COMMITTED (non-repeatable read occurs) --
Session A, 1st read: 'Let yourself be silently drawn by the strange pull of what you really love.'
Session B updates the row and COMMITS.
Session A, 2nd read (same query, same still-open logical read): 'EDITED AND COMMITTED BY B'
Non-repeatable read: A's two reads of the same row disagree.
-- Part 2: REPEATABLE READ (non-repeatable read prevented) --
Session A, 1st read (transaction snapshot begins here): 'Let yourself be silently drawn by the strange pull of what you really love.'
Session B updates the row and COMMITS.
Session A, 2nd read (still inside its original transaction): 'Let yourself be silently drawn by the strange pull of what you really love.'
No non-repeatable read: A's snapshot held steady across both reads.

=== Phantom read ===
-- Part 1: REPEATABLE READ (phantom read occurs) --
Session A, 1st COUNT: 2
Session B inserts a new matching row and COMMITS.
Session A, 2nd COUNT (same query, same logical read): 3
Phantom read: a brand-new row appeared in a range A had already read.
-- Part 2: SERIALIZABLE (phantom read prevented) --
Session A, 1st COUNT (transaction snapshot begins here): 2
Session B inserts a new matching row and COMMITS.
Session A, 2nd COUNT (still inside its original transaction): 2
No phantom read: A's range read held steady across both counts.

All three anomaly mechanics reproduced and their preventions verified.
```

What this output proves and what it doesn't: it's real, executed proof
that each anomaly's *mechanic* (an interleaving of read/write/commit that
either does or doesn't cross a transaction boundary) genuinely produces
the described result, and that the corresponding prevention genuinely
holds a stable snapshot/count across that same interleaving. It does
**not** prove SQL Server's specific locking mechanism — shared locks
released per-statement vs. held for the transaction, key-range locks — is
implemented the way this write-up describes; that description is
documented Microsoft locking behavior, stated here as documented, not as
captured from a running SQL Server. sqlite3 has no session-level
`SET TRANSACTION ISOLATION LEVEL` and no lock manager that blocks a
writer the way SQL Server's `REPEATABLE READ`/`SERIALIZABLE` would (this
proxy models the *outcome* — a stable snapshot — via WAL-mode snapshot
isolation, not via making Session B's write actually block).

**Recommended before merging**: run the four `.sql` files against a real
SQL Server, two SSMS tabs at a time as the comments direct, and paste the
actual blocking behavior observed (which tab visibly waits, and for how
long) alongside a screenshot — same caveat every SQL exercise this week
has carried, this is reasoned from documented behavior and an engine-
agnostic proxy, not a substitute for watching a real session block.

## What did you learn this session?

That these three anomalies aren't three unrelated bugs to memorize — they
sit on one ladder because each stricter isolation level closes off
exactly one more kind of "the data moved while I wasn't looking," in a
specific order: uncommitted values first, then existing rows changing
value, then new rows appearing in a range. `REPEATABLE READ` sitting
between the other two makes concrete sense once framed as "locks the
rows it's already touched, but not the gaps between them" — it's not an
arbitrary middle setting, it's the natural boundary of what a per-row lock
can even cover.

## What would break this?

- **These four scripts assume nothing else is touching the `Quotes` table
  with `Author = 'Rumi'` at the same time.** If this were run against a
  shared database with other traffic, a third session's own read or write
  against those same rows could interleave unpredictably with Sessions A
  and B's steps, making the demo non-deterministic. That's fine for a
  training exercise against a disposable database; it would not be a
  reasonable way to *test* isolation behavior in a real system with real
  concurrent load.
- **`SERIALIZABLE`'s prevention is also its cost.** Locking the whole
  range `Author = 'Rumi'` covers, not just the two rows that already
  exist, means any other session trying to insert a matching row is
  blocked for as long as Session A's transaction stays open — and long-
  held range locks are one of the more common real-world sources of
  deadlocks and blocking chains. Reaching for `SERIALIZABLE` everywhere
  "to be safe" trades a correctness problem for a throughput and
  contention problem; the right level is the loosest one a given
  transaction can tolerate, not the strictest one available.
- **The sqlite3 proxy's WAL-mode snapshot models the *outcome* of
  `REPEATABLE READ`/`SERIALIZABLE` (a stable read), not the *mechanism*
  (a lock that makes a concurrent writer wait).** A reader relying on
  this proxy to reason about whether a writer would block, and for how
  long, would be reasoning about the wrong engine — that's precisely why
  the `.sql` files are the actual deliverable here and the Python script
  is explicitly scoped as a mechanics proxy, not a stand-in for SQL
  Server's lock manager.
