-- Day 11 -- performance-profiling data setup.
--
-- A dedicated table, NOT dbo.Quotes -- deliberately, and for a stronger
-- reason than the usual "don't disturb other exercises". Day 7's joins/CTE/
-- window-function exercises and Day 9's isolation-level demos both assert on
-- exact row counts in dbo.Quotes ("111 rows", "Rumi count = 2"). Seeding
-- 50,000 synthetic rows into that table would silently invalidate the
-- captured evidence of three earlier days' work. Same reasoning Day 8 used
-- for QuoteEngagementEvents and Day 9 for DeadlockDemo.
--
-- Idempotent: safe to re-run.

IF OBJECT_ID('dbo.Day11Quotes', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Day11Quotes
    (
        Id     INT           NOT NULL IDENTITY(1,1) PRIMARY KEY,
        Author NVARCHAR(200) NOT NULL,
        Text   NVARCHAR(MAX) NOT NULL
    );
END;

-- Note what is deliberately ABSENT: any index on Author. That omission is
-- the point of this exercise, and it mirrors the real schema -- Quote is the
-- one entity in QuotesDbContext.OnModelCreating with no configuration at
-- all, so dbo.Quotes has no index on Author either. The clustered PK on Id
-- is the only index here, and it is useless for a WHERE Author = ... lookup.

-- ---------------------------------------------------------------------------
-- Seed 50,000 rows across 500 distinct authors (100 quotes each).
-- ---------------------------------------------------------------------------
-- Why this shape: the two numbers control the two halves of the problem
-- independently. 500 distinct authors sets how many per-author queries the
-- N+1 endpoint issues (501 including the author list). 50,000 rows sets how
-- much each of those queries has to scan when there is no index. Together:
-- 500 scans x 50,000 rows = 25,000,000 rows examined to return 500 counts.
--
-- Built with a cross join over sys.all_objects rather than a recursive CTE
-- purely for speed -- a recursive CTE would need OPTION (MAXRECURSION 0) and
-- generates rows one level at a time, which is slow enough at 50,000 to risk
-- the portal Query editor's timeout.

IF NOT EXISTS (SELECT 1 FROM dbo.Day11Quotes)
BEGIN
    WITH Numbers AS
    (
        SELECT TOP (50000)
            ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
        FROM sys.all_objects AS a
        CROSS JOIN sys.all_objects AS b
    )
    INSERT INTO dbo.Day11Quotes (Author, Text)
    SELECT
        'Perf Author ' + CAST(n % 500 AS VARCHAR(10)),
        'Perf seed quote ' + CAST(n AS VARCHAR(10)) + '. ' + REPLICATE('x', 200)
    FROM Numbers;
END;

-- ---------------------------------------------------------------------------
-- Confirm the shape before profiling anything.
-- ---------------------------------------------------------------------------
SELECT
    COUNT(*)                  AS TotalRows,
    COUNT(DISTINCT Author)    AS DistinctAuthors,
    COUNT(*) / COUNT(DISTINCT Author) AS RowsPerAuthor,
    COUNT(DISTINCT Author) + 1 AS QueriesTheNPlus1WillIssue
FROM dbo.Day11Quotes;

-- Expected: 50000 / 500 / 100 / 501.


-- Day 11 -- the SQL the slow endpoint emits, and its execution plan.
--
-- These are not hand-written illustrations. They are the statements EF Core
-- actually generates for the two endpoints in
-- QuotesApi/Extensions/DiagnosticsEndpointExtensions.cs, transcribed from the
-- API's own console log (Serilog logs
-- Microsoft.EntityFrameworkCore.Database.Command at Debug in Development --
-- see appsettings.Development.json, which is why no extra logging setup was
-- needed for this exercise), with the table name swapped to Day11Quotes so
-- they can be run here without touching dbo.Quotes.

-- ===========================================================================
-- PART A -- what the N+1 endpoint emits
-- ===========================================================================

-- Query 1 of 501: the distinct author list. Issued once.
SELECT DISTINCT [q].[Author]
FROM [Day11Quotes] AS [q];

-- Queries 2..501: this exact statement, 500 times, once per author, with only
-- the parameter value changing. This is the N+1.
DECLARE @__author_0 NVARCHAR(200) = N'Perf Author 7';

SELECT COUNT(*)
FROM [Day11Quotes] AS [q]
WHERE [q].[Author] = @__author_0;

-- ===========================================================================
-- PART B -- what the fixed endpoint emits: one statement, same answer
-- ===========================================================================
SELECT [q].[Author] AS [Author], COUNT(*) AS [QuoteCount]
FROM [Day11Quotes] AS [q]
GROUP BY [q].[Author];

-- ===========================================================================
-- PART C -- capturing the EXECUTION PLAN in Azure Portal's Query editor
-- ===========================================================================
-- Day 8 established, by testing rather than assuming, that this editor has no
-- "include actual execution plan" button and does not surface STATISTICS IO
-- in its Messages tab. So the plan has to come back as a RESULT SET instead
-- of as UI, and the editor only displays the LAST result set of a batch --
-- which, for once, works in our favour here.
--
-- SET STATISTICS XML ON makes each statement return its actual post-execution
-- plan as an extra result set after its own results. Running it with a single
-- statement therefore leaves the plan XML as the last result set, which is
-- what the grid shows. Click the cell to read the full XML.
--
-- (SET SHOWPLAN_XML ON would give the estimated plan with even less noise,
-- but it must be the only statement in its batch, and this editor gives no
-- way to send a GO batch separator -- so STATISTICS XML is the option that
-- actually works here.)

SET STATISTICS XML ON;

DECLARE @author NVARCHAR(200) = N'Perf Author 7';

SELECT COUNT(*)
FROM dbo.Day11Quotes
WHERE Author = @author;

SET STATISTICS XML OFF;

-- What to look for in that XML, and what it means:
--   PhysicalOp="Clustered Index Scan"  -- the whole table was read. With no
--                                        index on Author there is no seek
--                                        available; this is the finding.
--   EstimateRows / ActualRows          -- ~100 rows returned out of 50,000
--                                        read. Reading 500x more rows than
--                                        are returned is the cost.
--   <MissingIndexes>                   -- SQL Server's own recommendation.
--                                        If present, it names Author as the
--                                        index it wants, which is a stronger
--                                        piece of evidence than any argument
--                                        in the write-up: the engine itself
--                                        is asking for the fix.

-- ===========================================================================
-- PART D -- the same plan via Query Store, after the load test has run
-- ===========================================================================
-- More representative than PART C for one specific reason: this returns the
-- plan for the query as the API actually executed it thousands of times under
-- load, with the API's own parameter types and settings, rather than for a
-- statement typed by hand in a different session.
--
-- Day 9 found "Query performance insight" empty on this database because
-- nothing had generated enough history yet. A load test is exactly what fixes
-- that -- so run this AFTER bombardier/k6, not before.

SELECT TOP (10)
    qt.query_sql_text,
    rs.count_executions,
    CAST(rs.avg_duration / 1000.0 AS DECIMAL(10,2))  AS AvgDurationMs,
    CAST(rs.max_duration / 1000.0 AS DECIMAL(10,2))  AS MaxDurationMs,
    rs.avg_logical_io_reads                          AS AvgLogicalReads,
    CAST(p.query_plan AS XML)                        AS QueryPlanXml
FROM sys.query_store_query_text        AS qt
JOIN sys.query_store_query            AS q  ON q.query_text_id = qt.query_text_id
JOIN sys.query_store_plan             AS p  ON p.query_id      = q.query_id
JOIN sys.query_store_runtime_stats    AS rs ON rs.plan_id      = p.plan_id
WHERE qt.query_sql_text LIKE '%Day11Quotes%'
   OR qt.query_sql_text LIKE '%Quotes%'
ORDER BY rs.count_executions DESC;

-- avg_logical_io_reads is the number worth writing down next to the p99: it
-- is the database-side measure of the same problem the load test sees from
-- the outside, and unlike wall-clock latency it does not move run to run.


-- Day 11 -- the missing-index half of the fix, and the plan change it causes.
--
-- Run 01- first and keep its plan output: the whole value of this file is the
-- before/after comparison, and the "before" is only credible if it was
-- captured on the same table, same data, same session settings.

-- ===========================================================================
-- STEP 1 -- confirm what indexes exist right now (the "before" state)
-- ===========================================================================
SELECT
    i.name           AS IndexName,
    i.type_desc      AS IndexType,
    c.name           AS ColumnName
FROM sys.indexes       AS i
LEFT JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
LEFT JOIN sys.columns       AS c  ON c.object_id  = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID('dbo.Day11Quotes')
ORDER BY i.index_id, ic.key_ordinal;

-- Expected before the fix: exactly one row, the clustered PK on Id. Nothing
-- on Author -- which is why every per-author query scans.

-- ===========================================================================
-- STEP 2 -- ask SQL Server what index IT thinks is missing
-- ===========================================================================
-- Worth running before creating anything. This is the engine's own opinion,
-- accumulated from the queries actually run against this database, and it is
-- better evidence than a developer's reasoning about what "should" help.
SELECT
    mid.statement                         AS TableName,
    mid.equality_columns,
    mid.inequality_columns,
    mid.included_columns,
    migs.user_seeks,
    migs.avg_total_user_cost,
    migs.avg_user_impact                  AS EstimatedPercentImprovement
FROM sys.dm_db_missing_index_details        AS mid
JOIN sys.dm_db_missing_index_groups         AS mig  ON mig.index_handle = mid.index_handle
JOIN sys.dm_db_missing_index_group_stats    AS migs ON migs.group_handle = mig.index_group_handle
WHERE mid.statement LIKE '%Day11Quotes%'
ORDER BY migs.avg_user_impact DESC;

-- ===========================================================================
-- STEP 3 -- create the index
-- ===========================================================================
-- Nonclustered on Author alone. Deliberately NOT covering: no
-- INCLUDE (Text). The N+1 query is COUNT(*) filtered by Author, so it needs
-- to locate the matching rows and count them -- it never reads Text. Adding
-- Text to the index would make every leaf page carry ~200 characters it is
-- never asked for, which is precisely the mistake Day 8's covering-index task
-- warned about ("INCLUDE-ing too much, or the wrong thing, isn't free").
IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.Day11Quotes')
      AND name = 'IX_Day11Quotes_Author'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_Day11Quotes_Author
        ON dbo.Day11Quotes (Author);
END;

-- ===========================================================================
-- STEP 4 -- the same query, the same way, with the index in place
-- ===========================================================================
SET STATISTICS XML ON;

DECLARE @author NVARCHAR(200) = N'Perf Author 7';

SELECT COUNT(*)
FROM dbo.Day11Quotes
WHERE Author = @author;

SET STATISTICS XML OFF;

-- What should have changed in the plan XML:
--   PhysicalOp  "Clustered Index Scan"  ->  "Index Seek"
--   ActualRows read at the scan/seek    ->  ~100 instead of 50,000
--   <MissingIndexes>                    ->  gone (the engine got what it asked for)
--
-- What should NOT be claimed from this: that the endpoint is now fast. The
-- index fixes the SECOND of the two problems. 501 round trips is still 501
-- round trips -- each one is now cheap, but the per-request network and
-- command-processing overhead is unchanged. That is exactly why the API has
-- both /authors-quotes-nplus1 and /authors-quotes-grouped: the index and the
-- GROUP BY fix different halves, and profiling all four combinations
-- (index x strategy) is what separates the two effects instead of conflating
-- them.

-- ===========================================================================
-- STEP 5 -- the fixed endpoint's statement, with the index present
-- ===========================================================================
-- Worth capturing too, because the answer here is genuinely interesting: a
-- GROUP BY over every row does NOT benefit from the Author index the way the
-- filtered COUNT does -- it has to read the whole table either way. It may
-- use the index as a narrower path (the index is smaller than the table, so
-- scanning it beats scanning the clustered index), which is a different and
-- more subtle win than a seek.
SET STATISTICS XML ON;

SELECT Author, COUNT(*) AS QuoteCount
FROM dbo.Day11Quotes
GROUP BY Author;

SET STATISTICS XML OFF;

-- ===========================================================================
-- Cleanup, if this table is no longer wanted:
-- ===========================================================================
-- DROP INDEX IX_Day11Quotes_Author ON dbo.Day11Quotes;
-- DROP TABLE dbo.Day11Quotes;
-- Left commented rather than executed -- the table is 50,000 rows of
-- synthetic data in a Free-tier database, and keeping it means the plans
-- above stay reproducible for a mentor reviewing this later.
