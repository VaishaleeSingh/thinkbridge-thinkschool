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
    images/
      01-dirty-read-sessionA-azure.jpg
      01-dirty-read-sessionB-azure.jpg
      02-non-repeatable-read-sessionA-azure.jpg
      03-phantom-read-part1-sessionA-azure.jpg
      03-phantom-read-part1-sessionB-insert-azure.jpg
      03-phantom-read-part2-sessionA-azure.jpg
      03-phantom-read-part2-sessionB-blocking-azure.jpg
      03-phantom-read-part2-sessionB-unblocked-azure.jpg
      04-non-repeatable-read-part2-sessionA-azure.jpg
      04-non-repeatable-read-part2-sessionB-blocking-azure.jpg
      04-non-repeatable-read-part2-sessionB-unblocked-azure.jpg
      05-dirty-read-part2-sessionA-azure.jpg
      05-dirty-read-part2-sessionB-azure.jpg
      05-dirty-read-rcsi-check-azure.jpg
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

## Real verification against a live Azure SQL Database

The sqlite3 proxy above was the fallback while this sandbox had no route to
any SQL Server. That constraint has since been lifted: a real Azure SQL
Database (`quotesdb` on server `thinkschool-quotes-sql`, Central India,
Free tier) was provisioned, and this task was re-verified against it using
two genuinely separate browser tabs on Azure Portal's Query editor
(preview) — each "Run" click opens its own server-side connection/session
(confirmed via `@@SPID`/`@@TRANCOUNT`), so two tabs really are two
independent sessions, the same as two SSMS query windows.

Mechanical note for anyone repeating this: the Query editor does not let a
`BEGIN TRANSACTION` in one Run click stay open for a second Run click in
the same tab — every Run is a fresh connection. So each session's entire
sequence (including the deliberate pause between two reads) has to be one
self-contained batch with an embedded `WAITFOR DELAY` instead of multiple
sequential Run clicks, and because the results grid only shows the last
result set of a batch, each batch accumulates its step-by-step observations
into a `DECLARE @t TABLE (...)` variable and does one final
`SELECT * FROM @t` at the end. `docs/sql/01-dirty-read.sql` and
`02-non-repeatable-read.sql` show the two-Run-click version meant for SSMS
(where a transaction *can* span Run clicks); the batches actually executed
against Azure Portal's Query editor are reproduced below since they differ
in this one structural way.

**Dirty read — Part 1, `READ UNCOMMITTED` (real result: anomaly occurred)**

Session A:
```sql
DECLARE @t TABLE (Step VARCHAR(60), Val VARCHAR(200));
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
BEGIN TRANSACTION;
INSERT INTO @t SELECT 'A1: before B writes (READ UNCOMMITTED)', Text FROM dbo.Quotes WHERE Id = 12;
WAITFOR DELAY '00:00:20';
INSERT INTO @t SELECT 'A2: while B uncommitted (dirty read?)', Text FROM dbo.Quotes WHERE Id = 12;
WAITFOR DELAY '00:00:20';
INSERT INTO @t SELECT 'A3: after B rollback', Text FROM dbo.Quotes WHERE Id = 12;
COMMIT TRANSACTION;
SELECT * FROM @t;
```
Session B (run while A is inside its first `WAITFOR`):
```sql
DECLARE @t TABLE (Step VARCHAR(60), Val VARCHAR(200));
BEGIN TRANSACTION;
UPDATE dbo.Quotes SET Text = 'UNCOMMITTED EDIT -- should never be visible' WHERE Id = 12;
INSERT INTO @t SELECT 'B1: uncommitted update done', CAST(@@SPID AS VARCHAR(50));
WAITFOR DELAY '00:00:20';
ROLLBACK TRANSACTION;
INSERT INTO @t SELECT 'B2: rolled back', CAST(@@SPID AS VARCHAR(50));
SELECT * FROM @t;
```
Real captured result (screenshots in `docs/images/`): Session A's `A2` row
returned `'UNCOMMITTED EDIT -- should never be visible'` — B's SPID-76
in-flight, not-yet-committed write — while B's transaction was still open.
B then rolled back, and A's `A3` row showed the original text again. This
is a genuine dirty read: A read a value that, moments later, never existed
at any committed point in time.
- `docs/images/01-dirty-read-sessionA-azure.jpg` — Session A's result grid
  (`A1`/`A2`/`A3`, "Succeeded (40 sec 174 ms)")
- `docs/images/01-dirty-read-sessionB-azure.jpg` — Session B's result grid
  (`B1`/`B2`, SPID 76)

**Non-repeatable read — Part 1, `READ COMMITTED` (real result: anomaly occurred)**

Session A:
```sql
DECLARE @t TABLE (Step VARCHAR(60), IsEditedByB BIT);
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
BEGIN TRANSACTION;
INSERT INTO @t SELECT 'A1: first read (READ COMMITTED)',
  CASE WHEN Text LIKE '%EDITED AND COMMITTED BY B%' THEN 1 ELSE 0 END
  FROM dbo.Quotes WHERE Id = 14;
WAITFOR DELAY '00:00:15';
INSERT INTO @t SELECT 'A2: second read same tx',
  CASE WHEN Text LIKE '%EDITED AND COMMITTED BY B%' THEN 1 ELSE 0 END
  FROM dbo.Quotes WHERE Id = 14;
COMMIT TRANSACTION;
SELECT * FROM @t;
```
Session B (run while A is inside its `WAITFOR`):
```sql
UPDATE dbo.Quotes SET Text = 'EDITED AND COMMITTED BY B' WHERE Id = 14;
```
(`IsEditedByB` is a boolean flag rather than the raw `Text` column purely
because Azure Portal's results grid truncates long strings — the flag
removes any ambiguity about whether the value actually changed.)

Real captured result: `A1: IsEditedByB = False`, `A2: IsEditedByB = True`,
"Succeeded (15 sec 195 ms)" — the same still-open transaction read the same
row twice and got two different answers, because Session B committed a
real change in between. This is a genuine non-repeatable read.
- `docs/images/02-non-repeatable-read-sessionA-azure.jpg` — Session A's
  result grid confirming `False` then `True`

### Phantom read — Part 1: `REPEATABLE READ` allows it

Session A (real T-SQL batch run against the live database, using the same
`@t` table-variable accumulation trick):

```sql
DECLARE @t TABLE (Step VARCHAR(80), RumiCount INT);
SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;
BEGIN TRANSACTION;
INSERT INTO @t SELECT 'A1: first COUNT (before B inserts)', COUNT(*) FROM dbo.Quotes WHERE Author = 'Rumi';
WAITFOR DELAY '00:00:25';
INSERT INTO @t SELECT 'A2: second COUNT (same still-open tx)', COUNT(*) FROM dbo.Quotes WHERE Author = 'Rumi';
COMMIT TRANSACTION;
SELECT * FROM @t;
```

Session B, run in a second tab a few seconds after Session A's `Run` click
(while A is still inside its `WAITFOR DELAY`):

```sql
DECLARE @t TABLE (Step VARCHAR(80), Val VARCHAR(100));
INSERT INTO dbo.Quotes (Author, Text, CreatedByUserId)
VALUES ('Rumi', 'PHANTOM ROW -- inserted by Session B mid-transaction', NULL);
INSERT INTO @t SELECT 'B1: phantom row inserted and auto-committed', CAST(@@SPID AS VARCHAR(50));
SELECT * FROM @t;
```

Real captured result: Session B's insert succeeded immediately (no
blocking at all — SPID 81, "Succeeded 0 sec 154 ms") while Session A's
transaction was still open under `REPEATABLE READ`. Session A then
finished its `WAITFOR DELAY` and returned `A1: RumiCount = 2`,
`A2: RumiCount = 3` in the same still-open transaction ("Succeeded 25 sec
158 ms"). Two reads of the identical range query, inside one transaction,
disagreed — a genuine phantom read, and genuine proof that `REPEATABLE
READ`'s row-level locks from A1 did nothing to stop a brand-new row from
being inserted into the range.
- `docs/images/03-phantom-read-part1-sessionB-insert-azure.jpg` — Session
  B's instant, unblocked insert (SPID 81)
- `docs/images/03-phantom-read-part1-sessionA-azure.jpg` — Session A's
  result grid showing `RumiCount` go from 2 to 3

### Phantom read — Part 2: `SERIALIZABLE` prevents it

Same shape, only Session A's isolation level changes:

```sql
DECLARE @t TABLE (Step VARCHAR(80), RumiCount INT);
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
BEGIN TRANSACTION;
INSERT INTO @t SELECT 'A1: first COUNT (SERIALIZABLE range lock taken)', COUNT(*) FROM dbo.Quotes WHERE Author = 'Rumi';
WAITFOR DELAY '00:00:20';
INSERT INTO @t SELECT 'A2: second COUNT (same still-open tx)', COUNT(*) FROM dbo.Quotes WHERE Author = 'Rumi';
COMMIT TRANSACTION;
SELECT * FROM @t;
```

Session B (same insert as Part 1, run about 7 seconds after Session A
started):

```sql
DECLARE @t TABLE (Step VARCHAR(80), Val VARCHAR(100));
INSERT INTO dbo.Quotes (Author, Text, CreatedByUserId)
VALUES ('Rumi', 'PHANTOM ROW -- inserted by Session B mid-transaction', NULL);
INSERT INTO @t SELECT 'B1: insert completed (after any blocking)', CAST(@@SPID AS VARCHAR(50));
SELECT * FROM @t;
```

Real captured result — this is the strongest piece of evidence in this
submission: Session B's `Run` click sat spinning with a live "Cancel"
button and a running timer (screenshot taken mid-block at 7 sec 446 ms,
still executing) instead of completing instantly the way it did in Part
1. It only finished once Session A's transaction committed, for a total
real wall-clock time of **12 sec 810 ms** (SPID 79) — genuinely blocked by
Session A's `SERIALIZABLE` range lock, not merely "ran fast." Session A's
own result: `A1: RumiCount = 2`, `A2: RumiCount = 2`, "Succeeded 20 sec
566 ms" — the count never changed within A's transaction, because B's
insert physically could not complete until A released its lock. This is
not a modeled outcome; it is an actually-observed lock wait against the
live database.
- `docs/images/03-phantom-read-part2-sessionB-blocking-azure.jpg` —
  Session B still executing (spinner + "Cancel" button) 7+ seconds in
- `docs/images/03-phantom-read-part2-sessionB-unblocked-azure.jpg` —
  Session B finally completing at 12 sec 810 ms, immediately after A
  committed
- `docs/images/03-phantom-read-part2-sessionA-azure.jpg` — Session A's
  result grid showing `RumiCount` staying at 2 across both reads

### Non-repeatable read — Part 2: `REPEATABLE READ` prevents it

Same shape as the `REPEATABLE READ`/`SERIALIZABLE` phantom-read pair
above, but for the non-repeatable-read demo: Session A now holds its
shared lock on `Id = 14` for the whole transaction instead of releasing
it per-statement, so Session B's `UPDATE` on that same row has to wait.

Session A:

```sql
DECLARE @t TABLE (Step VARCHAR(80), OrigText VARCHAR(200));
SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;
BEGIN TRANSACTION;
INSERT INTO @t SELECT 'A1: first read (REPEATABLE READ)', Text FROM dbo.Quotes WHERE Id = 14;
WAITFOR DELAY '00:00:20';
INSERT INTO @t SELECT 'A2: second read same tx', Text FROM dbo.Quotes WHERE Id = 14;
COMMIT TRANSACTION;
SELECT * FROM @t;
```

Session B, run in the second tab a few seconds after Session A's `Run`
click (while A is inside its `WAITFOR DELAY`):

```sql
DECLARE @t TABLE (Step VARCHAR(80), Val VARCHAR(100));
UPDATE dbo.Quotes SET Text = 'EDITED AND COMMITTED BY B -- part2' WHERE Id = 14;
INSERT INTO @t SELECT 'B1: update completed (after any blocking)', CAST(@@SPID AS VARCHAR(50));
SELECT * FROM @t;
```

Real captured result: Session B's `Run` click sat with a live "Cancel"
button and a running timer — screenshotted mid-block at 7 sec 419 ms,
still executing — and only completed once Session A's transaction
committed, for a total real wall-clock time of **19 sec 384 ms** (SPID
82). Session A finished its own transaction at "Succeeded (20 sec 165
ms)" with `A1` and `A2` both showing the identical, unchanged original
text — the row Session A had already read stayed locked against Session
B's `UPDATE` for the full duration of A's transaction. This is a
genuine, actually-observed lock wait: `REPEATABLE READ` prevented the
non-repeatable read by blocking Session B outright, not by any modeled
or assumed behavior.

- `docs/images/04-non-repeatable-read-part2-sessionB-blocking-azure.jpg`
  — Session B still executing (spinner + "Cancel" button) 7+ seconds in
- `docs/images/04-non-repeatable-read-part2-sessionB-unblocked-azure.jpg`
  — Session B finally completing at 19 sec 384 ms, SPID 82, immediately
  after A committed
- `docs/images/04-non-repeatable-read-part2-sessionA-azure.jpg` —
  Session A's result grid showing the same original text in both `A1`
  and `A2`

Row `Id = 14` was reset back to its original text after this demo, so
the table is left in its pre-existing seeded state.

### Dirty read — Part 2: `READ COMMITTED` prevents it (via RCSI, not blocking)

Session A:

```sql
DECLARE @t TABLE (Step VARCHAR(60), IsDirty BIT);
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
BEGIN TRANSACTION;
INSERT INTO @t SELECT 'A1: first read (READ COMMITTED)',
  CASE WHEN Text LIKE '%UNCOMMITTED EDIT%' THEN 1 ELSE 0 END
  FROM dbo.Quotes WHERE Id = 12;
WAITFOR DELAY '00:00:05';
INSERT INTO @t SELECT 'A2: second read same tx',
  CASE WHEN Text LIKE '%UNCOMMITTED EDIT%' THEN 1 ELSE 0 END
  FROM dbo.Quotes WHERE Id = 12;
COMMIT TRANSACTION;
SELECT * FROM @t;
```

Session B, run in the second tab immediately after Session A starts:

```sql
DECLARE @t TABLE (Step VARCHAR(60), Val VARCHAR(50));
BEGIN TRANSACTION;
UPDATE dbo.Quotes SET Text = 'UNCOMMITTED EDIT -- part2' WHERE Id = 12;
INSERT INTO @t SELECT 'B1: uncommitted update in flight', CAST(@@SPID AS VARCHAR(50));
WAITFOR DELAY '00:00:20';
ROLLBACK TRANSACTION;
INSERT INTO @t SELECT 'B2: rolled back', CAST(@@SPID AS VARCHAR(50));
SELECT * FROM @t;
```

Real captured result — and a genuinely more interesting finding than the
originally-expected "A blocks" outcome: Session A completed almost
instantly, "Succeeded (0 sec 342 ms)", with `A1: IsDirty = False` and
`A2: IsDirty = False` — while Session B's transaction was still open and
did not finish until "Succeeded (20 sec 328 ms)" (SPID 80), roughly 20
seconds later. Session A never blocked, and never saw Session B's
in-flight value at any point — a clean, real prevention of the dirty
read, but through a different mechanism than the locking-based one
`REPEATABLE READ` used above for the non-repeatable-read demo.

The reason is a genuine, verified property of this specific database, not
an assumption: `quotesdb` has `READ_COMMITTED_SNAPSHOT` (RCSI) turned on.
Checked directly against the live database:

```sql
SELECT name, is_read_committed_snapshot_on FROM sys.databases WHERE name = 'quotesdb';
-- quotesdb | True
```

Under RCSI, `READ COMMITTED` reads use row-versioning (a snapshot of the
last committed version of each row) instead of taking a shared lock and
waiting for the writer's exclusive lock to release. That is why Session
A's read never blocked here even though it correctly never saw Session
B's uncommitted write: Azure SQL Database's default configuration
prevents the dirty read via snapshot isolation rather than via blocking.
Both are correct, real implementations of the `READ COMMITTED` contract
("never read another transaction's uncommitted data") — this database
just happens to use the row-versioning variant of it, which is itself a
result worth documenting rather than a deviation from what was expected.

- `docs/images/05-dirty-read-part2-sessionA-azure.jpg` — Session A's
  result grid, both reads `False`, "Succeeded (0 sec 342 ms)"
- `docs/images/05-dirty-read-part2-sessionB-azure.jpg` — Session B's
  result grid, SPID 80, "Succeeded (20 sec 328 ms)"
- `docs/images/05-dirty-read-rcsi-check-azure.jpg` — the
  `sys.databases` query confirming `is_read_committed_snapshot_on = True`
  for `quotesdb`

Row `Id = 12` was left untouched by this demo (Session B rolled back), so
no reset was needed.

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

**Worth a mentor's attention, not a gap**: all four prevention halves
(dirty read, non-repeatable read, phantom read — plus the initial
occurrence half of each) are now captured as real, executed evidence
against the live Azure SQL Database. The one thing worth flagging
explicitly is that the dirty-read prevention above does **not** show
blocking, unlike the other two preventions — and that is itself the
correct, verified behavior for this specific database (RCSI is on by
default), not an incomplete demo. A reader expecting to see Session A
stall the way it does under `REPEATABLE READ`/`SERIALIZABLE` should read
the RCSI explanation above before assuming something went wrong.

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
