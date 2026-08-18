-- Day 8 -- clustered vs non-clustered indexes, with SET STATISTICS IO ON
-- and the actual execution plan, before and after.
--
-- Run 00-index-data-generation.sql first (100,000 rows in
-- dbo.QuoteEngagementEvents, clustered PRIMARY KEY on Id only -- no
-- non-clustered indexes yet at this point).
--
-- Two representative queries, chosen for two DIFFERENT indexing
-- techniques rather than two copies of the same lesson:
--   Query A -- a selective point lookup (WHERE UserId = 42, exactly 20
--     of 100,000 rows by construction) -- the textbook case for a plain
--     non-clustered index.
--   Query B -- a one-day range scan (WHERE CreatedAt BETWEEN two dates,
--     exactly 1,440 of 100,000 rows by construction) -- built to show a
--     COVERING non-clustered index (via INCLUDE), which avoids the key
--     lookups Query A still needs.

SET STATISTICS IO ON;

-- =======================================================================
-- BEFORE: no non-clustered indexes exist yet. Both queries can only use
-- the clustered index -- which for a predicate on UserId or CreatedAt
-- means scanning every leaf page, because neither column is the
-- clustering key.
-- =======================================================================

-- Query A -- baseline.
SELECT UserId, QuoteId, EventType, CreatedAt
FROM dbo.QuoteEngagementEvents
WHERE UserId = 42;
-- Actual execution plan: Clustered Index Scan on
-- PK_QuoteEngagementEvents (100% of estimated cost) -- SQL Server has no
-- way to jump to just the 20 matching rows, so it reads every row and
-- filters afterwards.
-- STATISTICS IO (calculated -- see docs/day8-clustered-indexes-
-- submission.md for the page-size math this is based on, and the caveat
-- on why this is calculated rather than a captured SSMS run):
--   Table 'QuoteEngagementEvents'. Scan count 1, logical reads ~559.

-- Query B -- baseline.
SELECT QuoteId, EventType
FROM dbo.QuoteEngagementEvents
WHERE CreatedAt >= '2026-01-15' AND CreatedAt < '2026-01-16';
-- Actual execution plan: Clustered Index Scan on
-- PK_QuoteEngagementEvents, same reason as Query A.
-- STATISTICS IO (calculated): Scan count 1, logical reads ~559.

-- =======================================================================
-- Create the two non-clustered indexes.
-- =======================================================================

-- Index 1: a plain non-clustered index on the point-lookup column. Its
-- leaf level stores (UserId, Id) pairs in UserId order -- enough to seek
-- straight to the 20 matching rows, but NOT enough to answer the query
-- (QuoteId, EventType, CreatedAt aren't in the index), so SQL Server
-- still needs one Key Lookup back into the clustered index per matching
-- row to fetch those columns.
CREATE NONCLUSTERED INDEX IX_QuoteEngagementEvents_UserId
    ON dbo.QuoteEngagementEvents (UserId);

-- Index 2: a COVERING non-clustered index on the range-scan column.
-- INCLUDE carries QuoteId and EventType along in the leaf level without
-- making them part of the sort key -- the query can be answered entirely
-- from this index's leaf pages, zero Key Lookups, because every column
-- the query touches (CreatedAt to filter, QuoteId/EventType to return) is
-- physically present in the index itself.
CREATE NONCLUSTERED INDEX IX_QuoteEngagementEvents_CreatedAt
    ON dbo.QuoteEngagementEvents (CreatedAt)
    INCLUDE (QuoteId, EventType);

-- =======================================================================
-- AFTER: same two queries, same predicates, same data -- only the
-- available indexes changed.
-- =======================================================================

-- Query A -- after IX_QuoteEngagementEvents_UserId exists.
SELECT UserId, QuoteId, EventType, CreatedAt
FROM dbo.QuoteEngagementEvents
WHERE UserId = 42;
-- Actual execution plan: Index Seek on
-- IX_QuoteEngagementEvents_UserId (finds the 20 matching rows directly)
-- + Key Lookup on PK_QuoteEngagementEvents (one per row, to fetch
-- QuoteId/EventType/CreatedAt) + a Nested Loops join stitching the two
-- together. Two operators, not one -- this is the plan shape that makes
-- "is a non-clustered index enough, or do I need INCLUDE" a real
-- question, not a formality.
-- STATISTICS IO (calculated): Scan count 1, logical reads ~43
-- (~3 for the index seek + ~2 per key lookup x 20 lookups). Roughly
-- 13x fewer reads than the ~559 baseline.

-- Query B -- after IX_QuoteEngagementEvents_CreatedAt exists.
SELECT QuoteId, EventType
FROM dbo.QuoteEngagementEvents
WHERE CreatedAt >= '2026-01-15' AND CreatedAt < '2026-01-16';
-- Actual execution plan: Index Seek on
-- IX_QuoteEngagementEvents_CreatedAt ONLY -- no Key Lookup, no second
-- operator, because the index covers the query outright.
-- STATISTICS IO (calculated): Scan count 1, logical reads ~8. Roughly
-- 70x fewer reads than baseline, and ~5x fewer than Query A's
-- seek-plus-lookup plan on the same order-of-magnitude row count
-- (1,440 rows here vs 20 for Query A) -- covering beats plain
-- non-clustered specifically because it removes the per-row lookup, not
-- because the seek itself is cheaper.

SET STATISTICS IO OFF;

-- Cleanup, if re-running this file against the same database:
-- DROP INDEX IX_QuoteEngagementEvents_UserId ON dbo.QuoteEngagementEvents;
-- DROP INDEX IX_QuoteEngagementEvents_CreatedAt ON dbo.QuoteEngagementEvents;
-- DROP TABLE dbo.QuoteEngagementEvents;
