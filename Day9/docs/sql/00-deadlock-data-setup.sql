-- Day 9 -- deadlock demo setup.
--
-- A dedicated table, not dbo.Quotes -- deliberately, so this demo can't
-- disturb the seeded rows the Day 7 joins/CTE/window-function exercises
-- already depend on (same reasoning Day 8 used for QuoteEngagementEvents:
-- this is about a mechanic, not about Quotes specifically). Idempotent:
-- safe to re-run.

IF OBJECT_ID('dbo.DeadlockDemo', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DeadlockDemo
    (
        Id    INT          NOT NULL PRIMARY KEY,
        Value VARCHAR(50)  NOT NULL
    );
END;

-- Exactly two rows -- the two resources the deadlock will fight over.
IF NOT EXISTS (SELECT 1 FROM dbo.DeadlockDemo WHERE Id = 1)
    INSERT INTO dbo.DeadlockDemo (Id, Value) VALUES (1, 'Resource One -- original');

IF NOT EXISTS (SELECT 1 FROM dbo.DeadlockDemo WHERE Id = 2)
    INSERT INTO dbo.DeadlockDemo (Id, Value) VALUES (2, 'Resource Two -- original');

SELECT * FROM dbo.DeadlockDemo ORDER BY Id;
-- Expected: exactly 2 rows, Id 1 and Id 2, both with their "-- original"
-- text. Re-run the two UPDATE statements below by hand if either part of
-- this file below ever leaves a row edited:
--
-- UPDATE dbo.DeadlockDemo SET Value = 'Resource One -- original' WHERE Id = 1;
-- UPDATE dbo.DeadlockDemo SET Value = 'Resource Two -- original' WHERE Id = 2;
