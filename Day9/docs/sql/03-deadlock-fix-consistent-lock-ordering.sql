-- Day 9 -- the fix: consistent lock ordering removes the deadlock.
--
-- 01-deadlock-reproduction.sql deadlocked because Session A locked
-- Id 1 then wanted Id 2, while Session B locked Id 2 then wanted Id 1 --
-- opposite orders, which is what makes the circular wait possible in the
-- first place. The fix isn't a SQL feature (no special hint, no different
-- isolation level) -- it's a coding rule: every transaction that touches
-- both rows must acquire them in the SAME order. If both sessions always
-- go "Id 1 first, then Id 2", the second session to arrive just blocks
-- and waits its turn -- normal lock contention, not a deadlock, because
-- there is no longer a cycle for the deadlock monitor to find.
--
-- Two separate sessions/tabs again, same idea as before, but Session B's
-- statement order is now reversed to match Session A's.

-- Reset first, in case 01- left a row edited:
UPDATE dbo.DeadlockDemo SET Value = 'Resource One -- original' WHERE Id = 1;
UPDATE dbo.DeadlockDemo SET Value = 'Resource Two -- original' WHERE Id = 2;

-- --- SESSION A -- step 1 --------------------------------------------
BEGIN TRANSACTION;
UPDATE dbo.DeadlockDemo SET Value = 'Locked by A (ordered)' WHERE Id = 1;
-- A holds Id = 1, same as before. Leave open.

-- --- SESSION B -- step 1 --------------------------------------------
-- Paste into a SECOND tab, run a few seconds after Session A's step 1.
BEGIN TRANSACTION;
UPDATE dbo.DeadlockDemo SET Value = 'B wants Id 1 too (ordered)' WHERE Id = 1;
-- B now tries Id = 1 FIRST, same order Session A used -- this is the fix.
-- B simply BLOCKS here, waiting for A to release Id 1. No second resource
-- has been touched yet on either side, so there is nothing for a cycle to
-- form around.

-- --- SESSION A -- step 2 --------------------------------------------
-- Back in Session A's tab, while B sits blocked on step 1.
UPDATE dbo.DeadlockDemo SET Value = 'A wants Id 2 too (ordered)' WHERE Id = 2;
COMMIT TRANSACTION;
-- A acquires Id = 2 without contention (B never touched it), then commits
-- and releases BOTH locks. The instant A commits, Session B's blocked
-- step 1 unblocks and completes on its own.

-- --- SESSION B -- step 2 --------------------------------------------
-- B's step 1 has now completed (it was unblocked by A's commit above).
-- Continue B's same transaction:
UPDATE dbo.DeadlockDemo SET Value = 'B wants Id 2 too (ordered)' WHERE Id = 2;
COMMIT TRANSACTION;
-- B acquires Id = 2 (free now that A committed) and commits. Both
-- transactions complete in full -- nobody was picked as a deadlock
-- victim, nobody's transaction was rolled back. The only cost was B
-- waiting instead of erroring out.

-- =======================================================================
-- Cleanup / sanity check:
-- =======================================================================
UPDATE dbo.DeadlockDemo SET Value = 'Resource One -- original' WHERE Id = 1;
UPDATE dbo.DeadlockDemo SET Value = 'Resource Two -- original' WHERE Id = 2;
SELECT * FROM dbo.DeadlockDemo ORDER BY Id;

-- =======================================================================
-- REAL AZURE SQL DATABASE RESULT -- run live, not simulated
-- =======================================================================
-- Same two-tab technique as 01-deadlock-reproduction.sql's real run, same
-- @t-accumulating batch shape per session, with Session B's statement
-- order reversed to match Session A (both now touch Id = 1 first):
--
--   Session A: BEGIN TRAN -> lock Id=1 -> WAITFOR 20s -> lock Id=2 -> COMMIT
--   Session B: BEGIN TRAN -> lock Id=1 (waits on A) -> lock Id=2 -> COMMIT
--
-- Session A (tab 1) started first. Session B (tab 2) started a few
-- seconds later and immediately tried Id = 1 -- the same resource A was
-- already holding -- so B blocked on ordinary lock contention rather
-- than touching a second, different resource first.
--
-- Real outcome -- no deadlock, no 1205 error, both sessions committed:
--   Session A (SPID 77) -- result grid: "A1: locked Id=1" / "A2: got
--     Id=2 (ordered fix)", both SPID 77. Status bar: Succeeded
--     (21 sec 40 ms) -- matches the ~20s WAITFOR plus overhead.
--   Session B (SPID 71) -- result grid: "B1: got Id=1 (waited for A)" /
--     "B2: got Id=2", both SPID 71. Status bar: Succeeded (5 sec 894 ms)
--     -- B simply sat blocked on Id = 1 until A's COMMIT released it,
--     then finished immediately. No error, no rollback, no victim.
--
-- Screenshots:
--   docs/images/07-deadlock-fix-sessionA-azure.jpg  (SPID 77 result grid)
--   docs/images/07-deadlock-fix-sessionB-azure.jpg  (SPID 71 result grid)
--
-- This is the actual proof the exercise asks for: the only code change
-- between the deadlocking run and this run is the order of Session B's
-- two UPDATE statements -- no isolation-level change, no lock hint, no
-- retry logic. Making both sessions agree on "Id 1 before Id 2" removed
-- the cycle entirely; the second session to arrive now just waits its
-- turn, which is normal, expected contention rather than a failure.
