-- Day 9 -- reproduce a dirty read, then show the isolation level that
-- prevents it.
--
-- A dirty read is: Session A reads a row that Session B has changed but
-- NOT committed yet. If B later rolls back, A read a value that never
-- actually existed in the database at any committed point in time.
--
-- Run this as two SEPARATE connections/tabs in SSMS (or any client), not
-- as one script top to bottom -- that's the whole point of "open two
-- sessions". Each block below is labelled with which tab to paste it into
-- and in what order. Run dbo.Quotes/00-seed-data.sql first, in either
-- session, before starting Part 1.
--
-- Assumes the seed data from 00-seed-data.sql: exactly one row with
-- Author = 'Rumi' AND Text = 'The wound is the place where the Light
-- enters you.'

-- =======================================================================
-- PART 1 -- READ UNCOMMITTED allows the dirty read.
-- =======================================================================

-- --- SESSION A -- step 1 --------------------------------------------
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
BEGIN TRANSACTION;
SELECT Text FROM dbo.Quotes
WHERE Author = 'Rumi' AND Text LIKE 'The wound is the place%';
-- Expected: 'The wound is the place where the Light enters you.'
-- (the original, committed value -- confirms the starting point before B
-- touches anything). Leave this transaction open; do not COMMIT yet.

-- --- SESSION B -- step 2 --------------------------------------------
-- Paste into a SECOND query tab/connection, run AFTER Session A's step 1.
BEGIN TRANSACTION;
UPDATE dbo.Quotes
SET Text = 'UNCOMMITTED EDIT -- should never be visible to anyone else'
WHERE Author = 'Rumi' AND Text LIKE 'The wound is the place%';
-- Deliberately NOT committed or rolled back yet -- this is the
-- uncommitted change Session A is about to read.

-- --- SESSION A -- step 3 --------------------------------------------
-- Back in Session A's tab, run AFTER Session B's step 2, while B's
-- transaction is still open.
SELECT Text FROM dbo.Quotes
WHERE Author = 'Rumi' AND Text LIKE '%should never be visible%';
-- Under READ UNCOMMITTED, this SUCCEEDS immediately (no blocking) and
-- returns B's not-yet-committed text -- because READ UNCOMMITTED takes no
-- shared lock and ignores B's exclusive lock entirely (a "NOLOCK" read).
-- THIS is the dirty read: A just read a value with zero guarantee it will
-- ever be real.

-- --- SESSION B -- step 4 --------------------------------------------
ROLLBACK TRANSACTION;
-- B undoes the edit. The value Session A read in step 3 now never existed
-- at any committed point in time -- proving it was "dirty".

-- --- SESSION A -- step 5 --------------------------------------------
SELECT Text FROM dbo.Quotes
WHERE Author = 'Rumi' AND Text LIKE 'The wound is the place%';
-- Back to the original text -- confirms step 3's read was of a value that
-- has now vanished, which is the anomaly.
COMMIT TRANSACTION;

-- =======================================================================
-- PART 2 -- READ COMMITTED (SQL Server's default) prevents the dirty
-- read. Same steps, only the isolation level in Session A changes.
-- =======================================================================

-- --- SESSION A -- step 1 --------------------------------------------
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
BEGIN TRANSACTION;
SELECT Text FROM dbo.Quotes
WHERE Author = 'Rumi' AND Text LIKE 'The wound is the place%';
COMMIT TRANSACTION;
-- Commit and release straight away this time -- step 3 below needs A to
-- not be holding a lock of its own, so the only lock in play is B's.

-- --- SESSION B -- step 2 --------------------------------------------
BEGIN TRANSACTION;
UPDATE dbo.Quotes
SET Text = 'UNCOMMITTED EDIT -- should never be visible to anyone else'
WHERE Author = 'Rumi' AND Text LIKE 'The wound is the place%';
-- Still open, still uncommitted, same as Part 1.

-- --- SESSION A -- step 3 --------------------------------------------
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
SELECT Text FROM dbo.Quotes
WHERE Author = 'Rumi' AND Text LIKE '%should never be visible%';
-- Under READ COMMITTED this BLOCKS -- A's SELECT needs a shared lock, and
-- B is holding an exclusive lock from its uncommitted UPDATE. A's query
-- will sit waiting, not return B's value. (If this session has a lock
-- timeout set, it will time out here rather than return dirty data --
-- either outcome is "prevented", never a silent dirty read.)

-- --- SESSION B -- step 4 --------------------------------------------
-- Run this to unblock Session A's step 3 and finish the comparison.
ROLLBACK TRANSACTION;
-- The instant B rolls back, Session A's blocked SELECT in step 3 returns
-- zero rows (the LIKE '%should never be visible%' text never committed),
-- not B's uncommitted edit. That's the prevention: A either waits or sees
-- nothing dirty -- never the in-flight value.

-- =======================================================================
-- Cleanup / sanity check, after both parts:
-- =======================================================================
SELECT Text FROM dbo.Quotes
WHERE Author = 'Rumi' AND Text LIKE 'The wound is the place%';
-- Should be back to the original seed text in both parts -- neither
-- experiment should leave a permanent change.

-- =======================================================================
-- REAL AZURE SQL DATABASE RESULT for Part 2 (see the submission markdown,
-- section "Dirty read -- Part 2", for the exact self-contained batches
-- actually run against quotesdb via Azure Portal's Query editor):
--
-- Session A completed almost instantly ("Succeeded 0 sec 342 ms") and
-- never saw Session B's uncommitted write, in BOTH of its reads -- but it
-- did NOT block/wait the way this file's comments above describe. That is
-- because quotesdb has READ_COMMITTED_SNAPSHOT (RCSI) turned ON:
--
--   SELECT name, is_read_committed_snapshot_on FROM sys.databases
--   WHERE name = 'quotesdb';   -- quotesdb | True
--
-- Under RCSI, READ COMMITTED prevents the dirty read via row-versioning
-- (a snapshot of the last committed row version) instead of via the
-- shared-lock-blocks-on-exclusive-lock mechanism described above. Both
-- are correct, real implementations of the READ COMMITTED contract; this
-- specific Azure SQL Database just uses the non-blocking, row-versioned
-- one. Real screenshots: docs/images/05-dirty-read-part2-sessionA-azure.jpg,
-- 05-dirty-read-part2-sessionB-azure.jpg, 05-dirty-read-rcsi-check-azure.jpg.
-- =======================================================================
