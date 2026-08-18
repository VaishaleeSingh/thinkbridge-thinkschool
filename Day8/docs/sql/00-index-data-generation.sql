-- Day 8 -- clustered vs non-clustered indexes: table + ~100k rows of data.
--
-- A purpose-built table for this exercise (QuoteEngagementEvents), not
-- the real Quotes table -- the real Quotes table has 13 rows
-- (00-seed-sample-data.sql under Day7/piece2) and inflating it with fake
-- quotes to reach 100k rows would corrupt that fixture for no reason.
-- QuoteEngagementEvents models a plausible real feature this app doesn't
-- have yet: one row per view/like/share event against a quote -- exactly
-- the kind of high-volume, append-only table where indexing choices
-- actually matter, unlike Quotes itself (13 rows, any index is free).
--
-- QuoteId cycles through the 13 real seeded quotes so the table stays
-- referentially plausible even without an actual FK constraint (adding
-- one isn't the point of this exercise).

CREATE TABLE dbo.QuoteEngagementEvents (
    Id         INT IDENTITY(1,1) NOT NULL,
    QuoteId    INT NOT NULL,
    EventType  VARCHAR(20) NOT NULL,   -- 'view' | 'like' | 'share'
    UserId     INT NOT NULL,
    CreatedAt  DATETIME2 NOT NULL,
    CONSTRAINT PK_QuoteEngagementEvents PRIMARY KEY CLUSTERED (Id)
);
-- The clustered index: a PRIMARY KEY is clustered by default in SQL
-- Server unless told otherwise, and Id is the right column for it here --
-- ever-increasing, matches insert order, so new rows always land at the
-- end of the clustered B-tree instead of splitting pages in the middle
-- (the classic argument for clustering on a narrow, sequential key rather
-- than, say, UserId or CreatedAt directly).

-- ---------------------------------------------------------------------
-- Generate 100,000 rows without a loop (RBAR -- row by agonizing row --
-- would take real minutes at this volume). The standard set-based
-- pattern: double a small CROSS JOIN chain until it comfortably exceeds
-- the target row count, number the rows, then cap with WHERE.
-- L0=2 rows -> L1=4 -> L2=16 -> L3=256 -> L4=65,536 -> L5=131,072 (>100k).
-- ---------------------------------------------------------------------
;WITH L0 AS (SELECT 1 AS c UNION ALL SELECT 1),
L1 AS (SELECT 1 AS c FROM L0 A CROSS JOIN L0 B),
L2 AS (SELECT 1 AS c FROM L1 A CROSS JOIN L1 B),
L3 AS (SELECT 1 AS c FROM L2 A CROSS JOIN L2 B),
L4 AS (SELECT 1 AS c FROM L3 A CROSS JOIN L3 B),
L5 AS (SELECT 1 AS c FROM L4 A CROSS JOIN L0 B),   -- 65,536 x 2 = 131,072
Tally AS (
    SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N
    FROM L5
)
INSERT INTO dbo.QuoteEngagementEvents (QuoteId, EventType, UserId, CreatedAt)
SELECT
    ((N - 1) % 13) + 1                              AS QuoteId,    -- cycles the 13 real seeded quotes
    CASE N % 3 WHEN 0 THEN 'view' WHEN 1 THEN 'like' ELSE 'share' END AS EventType,
    ((N - 1) % 5000) + 1                             AS UserId,     -- 5,000 distinct simulated users -> exactly 20 rows/user
    DATEADD(MINUTE, N, '2026-01-01T00:00:00')         AS CreatedAt   -- 100,000 minutes = ~69 days, exactly 1,440 rows/calendar day
FROM Tally
WHERE N <= 100000;
-- No OPTION (MAXRECURSION ...) needed here -- unlike Day 7's date-series
-- CTE, none of L0-L5/Tally reference themselves (no UNION ALL back to the
-- same CTE name), so this isn't a recursive CTE at all, just five ordinary
-- CROSS JOINs chained together. MAXRECURSION only governs actual
-- self-referencing CTEs.

-- Sanity checks -- both cardinalities are exact by construction, not
-- approximate, which is what makes the two demo queries in
-- 01-clustered-vs-nonclustered-indexes.sql predictable:
--   SELECT COUNT(*) FROM dbo.QuoteEngagementEvents;                          -- 100000
--   SELECT COUNT(*) FROM dbo.QuoteEngagementEvents WHERE UserId = 42;        -- 20
--   SELECT COUNT(*) FROM dbo.QuoteEngagementEvents
--       WHERE CreatedAt >= '2026-01-15' AND CreatedAt < '2026-01-16';        -- 1440
