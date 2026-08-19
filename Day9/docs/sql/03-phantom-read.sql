-- Day 9 -- reproduce a phantom read, then show the isolation level that
-- prevents it.
--
-- A phantom read is: Session A runs a RANGE query (a WHERE that could
-- match a variable number of rows, not one specific row by key) twice in
-- one transaction, and gets a different ROW COUNT the second time,
-- because Session B inserted (or deleted) a row that matches A's filter
-- and committed it in between. Note the distinction from
-- 02-non-repeatable-read.sql: that one was the same existing row changing
-- value; this one is the SET of matching rows changing size -- a brand
-- new row appearing that satisfies a filter A already ran.
--
-- Two separate sessions again, run in the labelled order. Run
-- 00-seed-data.sql first, in either session, before Part 1 -- this demo
-- depends on the seed's exact starting count of 2 Rumi quotes.

-- =======================================================================
-- PART 1 -- REPEATABLE READ allows the phantom read.
--
-- This is the one anomaly REPEATABLE READ does NOT stop. It locks the
-- specific ROWS a query already read, so a second read of an unchanged
-- row is protected (that's 02-'s demo) -- but it takes no lock on the
-- "gaps" a new row could be inserted into, so nothing stops brand new
-- rows from appearing.
-- =======================================================================

-- --- SESSION A -- step 1 --------------------------------------------
SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;
BEGIN TRANSACTION;
SELECT COUNT(*) AS RumiQuoteCount FROM dbo.Quotes WHERE Author = 'Rumi';
-- Expected: 2 (the seed rows). REPEATABLE READ has now locked those 2
-- existing rows against being changed or deleted -- but has not locked
-- the "range" itself against new rows being added.

-- --- SESSION B -- step 2 --------------------------------------------
-- Paste into a SECOND tab/connection, run AFTER Session A's step 1.
INSERT INTO dbo.Quotes (Author, Text, CreatedByUserId)
VALUES ('Rumi', 'PHANTOM ROW -- inserted by Session B mid-transaction', NULL);
-- Succeeds immediately, with no blocking -- REPEATABLE READ's row locks
-- from step 1 don't cover a row that didn't exist yet. Auto-commits (no
-- explicit transaction wrapper), so this is a real, permanent, committed
-- new row the instant it runs.

-- --- SESSION A -- step 3 --------------------------------------------
-- Back in Session A's tab, same still-open transaction from step 1.
SELECT COUNT(*) AS RumiQuoteCount FROM dbo.Quotes WHERE Author = 'Rumi';
-- Under REPEATABLE READ, this returns 3 -- a different count than step
-- 1's 2, from the SAME query, in the SAME still-open transaction. THIS is
-- the phantom read: no row A already had changed underneath it (that
-- would be 02-'s anomaly); an entirely new row phantom-appeared into a
-- result set A had already computed.
COMMIT TRANSACTION;

-- =======================================================================
-- PART 2 -- SERIALIZABLE prevents the phantom read. Same steps; A now
-- takes a range (key-range) lock covering the gaps a matching row could
-- be inserted into, not just the rows that already exist.
-- =======================================================================

-- Reset before Part 2 (Part 1 left an extra row behind):
DELETE FROM dbo.Quotes WHERE Text LIKE 'PHANTOM ROW%';

-- --- SESSION A -- step 1 --------------------------------------------
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
BEGIN TRANSACTION;
SELECT COUNT(*) AS RumiQuoteCount FROM dbo.Quotes WHERE Author = 'Rumi';
-- Expected: 2 again. SERIALIZABLE takes a range lock for
-- "Author = 'Rumi'" covering not just these 2 rows but the whole
-- key-range they live in, so nothing matching that filter can be
-- inserted until A finishes.

-- --- SESSION B -- step 2 --------------------------------------------
INSERT INTO dbo.Quotes (Author, Text, CreatedByUserId)
VALUES ('Rumi', 'PHANTOM ROW -- inserted by Session B mid-transaction', NULL);
-- This BLOCKS this time -- the new row would fall inside the range A's
-- SERIALIZABLE read has locked, so B's INSERT sits waiting instead of
-- committing.

-- --- SESSION A -- step 3 --------------------------------------------
SELECT COUNT(*) AS RumiQuoteCount FROM dbo.Quotes WHERE Author = 'Rumi';
-- Still 2 -- unchanged, because B's INSERT hasn't been able to complete
-- yet. A's second range read matches its first, which is the prevention:
-- no phantom.
COMMIT TRANSACTION;
-- Committing releases A's range lock, which unblocks Session B's step 2
-- immediately -- B's INSERT then runs and commits on its own, and the
-- count only becomes 3 for whoever queries it AFTER A is done.

-- =======================================================================
-- Cleanup / sanity check, after both parts:
-- =======================================================================
DELETE FROM dbo.Quotes WHERE Text LIKE 'PHANTOM ROW%';
SELECT COUNT(*) AS RumiQuoteCount FROM dbo.Quotes WHERE Author = 'Rumi';
-- Should be back to 2 -- neither experiment should leave a permanent
-- change.
