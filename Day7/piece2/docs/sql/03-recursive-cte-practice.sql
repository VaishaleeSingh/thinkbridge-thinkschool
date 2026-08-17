-- Day 7 -- recursive CTE fluency practice.
--
-- The exercise asks to get fluent in "recursive + non-recursive CTEs".
-- 01-author-quote-summary.sql covers the non-recursive half against the
-- app's real schema. A recursive CTE walks a self-referential
-- relationship (a row pointing back to another row of the same table) --
-- and this schema genuinely has none: Quotes, Users, Collections and
-- CollectionItem are all flat, with no parent/child column anywhere
-- (no category tree, no threaded replies, no folder hierarchy). Bolting a
-- fake hierarchy onto the schema just to have something to recurse over
-- would be schema/feature work this task never asked for, so this file
-- practices the syntax standalone instead, with a note on where it would
-- actually earn its place in this app.
--
-- Run standalone -- no dependency on 00-seed-sample-data.sql.

-- -----------------------------------------------------------------------
-- 1) The canonical recursive CTE: a number sequence.
-- -----------------------------------------------------------------------
-- Anchor member (SELECT 1) runs once; the recursive member (SELECT n + 1
-- ... WHERE n < 10) re-runs against the CTE's own previous result set
-- until its WHERE clause stops matching. T-SQL requires an explicit
-- termination guard here for real: without the "WHERE n < 10" -- or with
-- one that can never become false -- this does not error, it just keeps
-- recursing until it hits the plan's MAXRECURSION limit (100 by default)
-- and THEN errors. OPTION (MAXRECURSION n) below is not decoration, it's
-- an explicit, visible cap instead of relying on the silent default.
WITH Numbers AS (
    SELECT 1 AS n

    UNION ALL

    SELECT n + 1
    FROM Numbers
    WHERE n < 10
)
SELECT n
FROM Numbers
OPTION (MAXRECURSION 100);
-- Expected: 1, 2, 3, ..., 10.

-- -----------------------------------------------------------------------
-- 2) The practical version: a date series, for gap-filling a report.
-- -----------------------------------------------------------------------
-- This is the shape that would actually earn a place in this app once a
-- Quotes.CreatedAt column exists: "how many quotes were created per day
-- this month" is a GROUP BY on CreatedAt's date part -- but GROUP BY only
-- ever produces a row for a day that had at least one quote. A day with
-- zero quotes doesn't appear as 0, it doesn't appear at all, which is a
-- real (and easy to miss) difference between "nothing happened" and
-- "we forgot to check". Generating the full date range first, THEN LEFT
-- JOINing the real aggregate onto it, is exactly the fix -- the same
-- LEFT JOIN vs INNER JOIN distinction 02-join-practice.sql demonstrates
-- against Quotes/Users, applied here to make gaps visible instead of
-- silently absent.
WITH DateSeries AS (
    SELECT CAST('2026-08-01' AS date) AS ReportDate

    UNION ALL

    SELECT DATEADD(DAY, 1, ReportDate)
    FROM DateSeries
    WHERE ReportDate < '2026-08-31'
)
SELECT
    ds.ReportDate
    -- , COUNT(q.Id) AS QuoteCount
    -- once Quotes.CreatedAt exists:
    -- FROM DateSeries AS ds
    -- LEFT JOIN dbo.Quotes AS q ON CAST(q.CreatedAt AS date) = ds.ReportDate
    -- GROUP BY ds.ReportDate
FROM DateSeries AS ds
ORDER BY ds.ReportDate
OPTION (MAXRECURSION 100);
-- Expected: 31 rows, 2026-08-01 through 2026-08-31 inclusive.
