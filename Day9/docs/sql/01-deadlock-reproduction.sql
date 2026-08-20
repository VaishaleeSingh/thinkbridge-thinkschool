-- Day 9 -- force a classic two-resource deadlock, then let SQL Server's
-- deadlock monitor resolve it by killing one session.
--
-- The shape: Session A locks Resource 1, then (after a pause) wants
-- Resource 2. Session B locks Resource 2, then (after a pause) wants
-- Resource 1. Neither can get what it's waiting for because the other
-- is holding it -- a circular wait. SQL Server's lock monitor polls for
-- exactly this cycle (default every 5 seconds) and picks a victim to
-- kill with error 1205 so the other session can proceed.
--
-- Two separate sessions/tabs, run in the labelled order. Run
-- 00-deadlock-data-setup.sql first, in either session.

-- --- SESSION A -- step 1 --------------------------------------------
BEGIN TRANSACTION;
UPDATE dbo.DeadlockDemo SET Value = 'Locked by A' WHERE Id = 1;
-- A now holds an exclusive lock on Id = 1. Leave this transaction open;
-- do NOT commit yet. Wait a few seconds, then run Session B's step 1
-- in a second tab, then come back here for step 2.

-- --- SESSION B -- step 1 --------------------------------------------
-- Paste into a SECOND tab/connection, run a few seconds after Session A's
-- step 1 (while A's transaction is still open).
BEGIN TRANSACTION;
UPDATE dbo.DeadlockDemo SET Value = 'Locked by B' WHERE Id = 2;
-- B now holds an exclusive lock on Id = 2. Leave this open too.

-- --- SESSION A -- step 2 --------------------------------------------
-- Back in Session A's tab, immediately after Session B's step 1.
UPDATE dbo.DeadlockDemo SET Value = 'A wants Id 2 too' WHERE Id = 2;
-- A now BLOCKS -- it wants Id = 2, which B is holding.

-- --- SESSION B -- step 2 --------------------------------------------
-- Immediately after Session A's step 2, back in Session B's tab.
UPDATE dbo.DeadlockDemo SET Value = 'B wants Id 1 too' WHERE Id = 1;
-- B now ALSO blocks -- it wants Id = 1, which A is holding. This is the
-- circular wait: A waits on B, B waits on A, neither can ever finish.
-- Within a few seconds SQL Server's deadlock monitor detects the cycle
-- and kills one session (the "victim", typically the one with the
-- cheaper transaction to roll back) with:
--   Msg 1205, Level 13, State 51
--   Transaction (Process ID N) was deadlocked on lock resources with
--   another process and has been chosen as the deadlock victim. Rerun
--   the transaction.
-- The victim's transaction is automatically rolled back. The surviving
-- session's blocked UPDATE then completes normally, and it must be
-- explicitly COMMITted or ROLLBACKed.

-- --- Whichever session survives -- finish it -------------------------
-- COMMIT TRANSACTION;  -- or ROLLBACK TRANSACTION; -- run in the surviving
-- session's tab once its blocked UPDATE completes.

-- =======================================================================
-- Cleanup / sanity check, after the deadlock resolves:
-- =======================================================================
UPDATE dbo.DeadlockDemo SET Value = 'Resource One -- original' WHERE Id = 1;
UPDATE dbo.DeadlockDemo SET Value = 'Resource Two -- original' WHERE Id = 2;
SELECT * FROM dbo.DeadlockDemo ORDER BY Id;
-- Both rows back to their original text -- neither experiment should
-- leave a permanent change.

-- =======================================================================
-- REAL AZURE SQL DATABASE RESULT -- run live, not simulated
-- =======================================================================
-- Reproduced against the live quotesdb (thinkschool-quotes-sql, Azure SQL
-- Database) using two separate Azure Portal Query editor tabs, each
-- authenticated as its own session. Because the portal's editor only
-- displays the last statement's result set, each session's actual batch
-- wrapped the steps above in a table variable so every step's outcome
-- (which step ran, and @@SPID) is visible together at the end:
--
--   DECLARE @t TABLE (Step VARCHAR(60), Val VARCHAR(100));
--   BEGIN TRANSACTION;
--   UPDATE dbo.DeadlockDemo SET Value = '...' WHERE Id = <own resource first>;
--   INSERT INTO @t SELECT '<label>', CAST(@@SPID AS VARCHAR(50));
--   WAITFOR DELAY '...';               -- staggered so the cycle forms
--   UPDATE dbo.DeadlockDemo SET Value = '...' WHERE Id = <other resource>;
--   INSERT INTO @t SELECT '<label>', CAST(@@SPID AS VARCHAR(50));
--   COMMIT TRANSACTION;
--   SELECT * FROM @t;
--
-- Session A (tab 1) started first: locked Id = 1, waited 20 seconds
-- (WAITFOR DELAY '00:00:20'), then tried Id = 2.
-- Session B (tab 2) started ~6 seconds later: locked Id = 2, waited only
-- 2 seconds, then tried Id = 1 -- opposite acquisition order from A, which
-- is what forces the cycle.
--
-- Real outcome:
--   Session A (SPID 69) -- SURVIVED. Result grid showed both steps:
--     "A1: locked Id=1" / "A2: got Id=2 (survived)", both SPID 69.
--     Status bar: Succeeded (24 sec 272 ms).
--   Session B (SPID 76) -- CHOSEN AS VICTIM. Exact error text returned by
--     Azure SQL Database:
--       "Transaction (Process ID 76) was deadlocked on lock resources
--        with another process and has been chosen as the deadlock
--        victim. Rerun the transaction."
--     Status bar: Failure (18 sec 344 ms). Session B's transaction was
--     automatically rolled back by the engine -- no COMMIT/ROLLBACK was
--     issued manually.
--
-- Screenshots:
--   docs/images/06-deadlock-sessionA-survivor-azure.jpg  (SPID 69 result grid)
--   docs/images/06-deadlock-sessionB-victim-azure.jpg    (SPID 76 error message)
--
-- This confirms the classic two-resource deadlock is real on Azure SQL
-- Database, not just an on-prem SQL Server behavior -- the PaaS engine
-- runs the same lock monitor and victim-selection logic.
