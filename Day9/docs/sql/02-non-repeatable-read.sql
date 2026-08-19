-- Day 9 -- reproduce a non-repeatable read, then show the isolation
-- level that prevents it.
--
-- A non-repeatable read is: Session A reads the SAME row twice inside one
-- transaction, and gets two different values, because Session B changed
-- and COMMITTED that row in between A's two reads. Unlike a dirty read,
-- the value A sees the second time is real and committed -- the anomaly
-- is that A's own transaction isn't looking at a stable snapshot of that
-- row for its own duration.
--
-- Two separate sessions again, run in the labelled order. Run
-- 00-seed-data.sql first, in either session, before Part 1.
--
-- Uses the second seeded row this time (deliberately different row from
-- 01-dirty-read.sql, so the two demo files don't collide if run in either
-- order or re-run against the same database): Author = 'Rumi', Text =
-- 'Let yourself be silently drawn by the strange pull of what you really
-- love.'

-- =======================================================================
-- PART 1 -- READ COMMITTED allows the non-repeatable read.
-- =======================================================================

-- --- SESSION A -- step 1 --------------------------------------------
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
BEGIN TRANSACTION;
SELECT Text FROM dbo.Quotes
WHERE Author = 'Rumi' AND Text LIKE 'Let yourself be silently drawn%';
-- Expected: 'Let yourself be silently drawn by the strange pull of what
-- you really love.' -- first read, inside A's still-open transaction.
-- READ COMMITTED takes and releases its shared lock per-statement, not
-- for the whole transaction -- that release is exactly what lets B in
-- next.

-- --- SESSION B -- step 2 --------------------------------------------
-- Paste into a SECOND tab/connection, run AFTER Session A's step 1.
UPDATE dbo.Quotes
SET Text = 'Let yourself be silently drawn -- EDITED AND COMMITTED BY B'
WHERE Author = 'Rumi' AND Text LIKE 'Let yourself be silently drawn%';
-- No explicit transaction wrapper needed -- an un-wrapped UPDATE
-- auto-commits immediately in SQL Server, which is exactly what this
-- anomaly needs: a real, permanent, committed change between A's two
-- reads (not an in-flight one, which would be the dirty-read demo
-- instead).

-- --- SESSION A -- step 3 --------------------------------------------
-- Back in Session A's tab, same still-open transaction from step 1.
SELECT Text FROM dbo.Quotes
WHERE Author = 'Rumi' AND Text LIKE '%EDITED AND COMMITTED BY B%';
-- Under READ COMMITTED, this returns B's new, committed text -- A's
-- transaction saw two different values for the same logical row without
-- ever having made a second real-world "read the row" decision of its
-- own. THIS is the non-repeatable read: A can't rely on its own earlier
-- read staying true for the rest of its transaction.
COMMIT TRANSACTION;

-- =======================================================================
-- PART 2 -- REPEATABLE READ prevents the non-repeatable read. Same
-- steps; A holds its shared lock for the whole transaction instead of
-- releasing it after each statement, which blocks B's UPDATE outright.
-- =======================================================================

-- Reset the row before Part 2 (Part 1 left it in B's edited state):
-- UPDATE dbo.Quotes SET Text = 'Let yourself be silently drawn by the
-- strange pull of what you really love.' WHERE Author = 'Rumi' AND Text
-- LIKE '%EDITED AND COMMITTED BY B%';

-- --- SESSION A -- step 1 --------------------------------------------
SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;
BEGIN TRANSACTION;
SELECT Text FROM dbo.Quotes
WHERE Author = 'Rumi' AND Text LIKE 'Let yourself be silently drawn%';
-- Same first read as Part 1 -- but REPEATABLE READ keeps the shared lock
-- on this row held until A's transaction ends, instead of releasing it
-- immediately.

-- --- SESSION B -- step 2 --------------------------------------------
UPDATE dbo.Quotes
SET Text = 'Let yourself be silently drawn -- EDITED AND COMMITTED BY B'
WHERE Author = 'Rumi' AND Text LIKE 'Let yourself be silently drawn%';
-- This BLOCKS this time -- B's UPDATE needs an exclusive lock on the same
-- row A is still holding a shared lock on, and REPEATABLE READ won't
-- release that shared lock until A commits or rolls back. B's tab will
-- sit waiting here.

-- --- SESSION A -- step 3 --------------------------------------------
SELECT Text FROM dbo.Quotes
WHERE Author = 'Rumi' AND Text LIKE 'Let yourself be silently drawn%';
-- Still the original text -- unchanged, because B's UPDATE hasn't been
-- able to run yet. A's second read inside the same transaction matches
-- its first, which is the prevention: the read is now repeatable.
COMMIT TRANSACTION;
-- Committing releases A's shared lock, which unblocks Session B's step 2
-- immediately -- B's UPDATE then runs and commits on its own.

-- =======================================================================
-- Cleanup / sanity check, after both parts:
-- =======================================================================
UPDATE dbo.Quotes
SET Text = 'Let yourself be silently drawn by the strange pull of what you really love.'
WHERE Author = 'Rumi' AND Text LIKE 'Let yourself be silently drawn%';
-- Restores the original seed text, whichever part left it edited.

-- =======================================================================
-- REAL AZURE SQL DATABASE RESULT for Part 2 (see the submission markdown,
-- section "Non-repeatable read -- Part 2", for the exact self-contained
-- batches actually run against quotesdb, Id = 14, via Azure Portal's
-- Query editor):
--
-- Session B's Run click genuinely blocked -- a live "Cancel" button and
-- running timer, screenshotted mid-block at 7 sec 419 ms -- and only
-- completed once Session A committed, for a real total of 19 sec 384 ms
-- (SPID 82). Session A finished at "Succeeded 20 sec 165 ms" with both
-- reads showing the identical, unchanged original text. This is a real,
-- observed lock wait: REPEATABLE READ's shared lock on the already-read
-- row genuinely blocked Session B's UPDATE for the full duration of A's
-- transaction, exactly as described above -- not a modeled outcome.
-- Real screenshots: docs/images/04-non-repeatable-read-part2-sessionA-azure.jpg,
-- 04-non-repeatable-read-part2-sessionB-blocking-azure.jpg,
-- 04-non-repeatable-read-part2-sessionB-unblocked-azure.jpg.
-- =======================================================================
