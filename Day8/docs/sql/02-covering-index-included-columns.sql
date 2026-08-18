-- Day 8 -- covering indexes + INCLUDEd columns.
--
-- Separate task from 01-clustered-vs-nonclustered-indexes.sql, but built on
-- the same table -- assumes dbo.QuoteEngagementEvents already exists with
-- ~100,000 rows, as created by docs/sql/00-index-data-generation.sql
-- (Task 1). Not recreated here to avoid duplicating that generator; this
-- file only adds new indexes and queries.
--
-- The point of THIS task, specifically: take one query that starts out
-- doing a Key Lookup, then fix that same query -- not a different one --
-- by widening its index with INCLUDE, and prove the lookup is gone from
-- the plan. (01- already showed a plain vs. covering index side by side on
-- two DIFFERENT queries; this task is the same lesson done as a single
-- query's before/after, which is the shape the exercise specifically asks
-- for -- "take A query doing a key lookup, add INCLUDE to eliminate it".)
--
-- Different predicate from 01- as well, deliberately: a composite
-- (QuoteId, CreatedAt) filter -- "engagement events for one quote on one
-- day" -- rather than either of 01-'s single-column predicates. This
-- exercises a composite seek key, not just a single-column one.

SET STATISTICS IO ON;

-- =======================================================================
-- The query this whole file is about. Selects two columns
-- (EventType, UserId) that are NOT part of the filter -- that's what will
-- force a Key Lookup once an index exists on just the filter columns.
-- =======================================================================
-- SELECT EventType, UserId
-- FROM dbo.QuoteEngagementEvents
-- WHERE QuoteId = 5
--   AND CreatedAt >= '2026-01-15' AND CreatedAt < '2026-01-16';
-- Matching rows: 111 (exact, by construction -- QuoteId cycles 1-13 evenly,
-- CreatedAt is 1,440 rows/calendar day, so ~1,440/13 ≈ 111 for one quote on
-- one day).

-- =======================================================================
-- STEP 0 -- baseline, before either index in this file exists.
-- =======================================================================
SELECT EventType, UserId
FROM dbo.QuoteEngagementEvents
WHERE QuoteId = 5
  AND CreatedAt >= '2026-01-15' AND CreatedAt < '2026-01-16';
-- Actual execution plan: Clustered Index Scan on PK_QuoteEngagementEvents
-- -- no index yet on QuoteId or CreatedAt in this file's scope (assume
-- 01-'s IX_QuoteEngagementEvents_CreatedAt from Task 1 doesn't exist
-- either, since this task is reviewed independently of Task 1's PR).
-- STATISTICS IO (calculated -- see docs/day8-covering-indexes-
-- submission.md for the page-math and the caveat on why this is
-- calculated, not a captured SSMS run): Scan count 1, logical reads ~559.

-- =======================================================================
-- STEP 1 -- add a PLAIN non-clustered index on just the filter columns.
-- This is the "obvious first index" someone reaches for -- it seeks
-- straight to the matching rows, but doesn't carry EventType or UserId,
-- so SQL Server still needs a Key Lookup per row to fetch them.
-- =======================================================================
CREATE NONCLUSTERED INDEX IX_QuoteEngagementEvents_QuoteId_CreatedAt
    ON dbo.QuoteEngagementEvents (QuoteId, CreatedAt);

-- Same query, same predicate -- only the available index changed.
SELECT EventType, UserId
FROM dbo.QuoteEngagementEvents
WHERE QuoteId = 5
  AND CreatedAt >= '2026-01-15' AND CreatedAt < '2026-01-16';
-- Actual execution plan: Index Seek on
-- IX_QuoteEngagementEvents_QuoteId_CreatedAt (finds the 111 matching rows
-- directly using BOTH key columns -- a composite seek, not just a seek on
-- QuoteId with CreatedAt filtered afterwards) + Key Lookup on
-- PK_QuoteEngagementEvents (one per row, to fetch EventType/UserId) +
-- Nested Loops joining the two. THIS is the "query doing a key lookup"
-- the exercise means -- two operators, not one.
-- STATISTICS IO (calculated): Scan count 1, logical reads ~225
-- (~3 for the index seek covering all 111 rows in a single leaf page,
-- since 111 << ~503 rows/page + 2 tree levels, + ~2 pages per key lookup x
-- 111 lookups). Already ~2.5x fewer reads than the ~559 baseline scan --
-- but the exercise isn't done, because the Key Lookup is still there.

-- =======================================================================
-- STEP 2 -- eliminate the Key Lookup: widen the SAME index with INCLUDE,
-- rather than leaving the plain version in place alongside a second one.
-- Two indexes on the same leading columns doing overlapping jobs is dead
-- weight on every write; the fix is to make this one index carry
-- everything the query needs, not to add a competing index.
-- =======================================================================
DROP INDEX IX_QuoteEngagementEvents_QuoteId_CreatedAt
    ON dbo.QuoteEngagementEvents;

CREATE NONCLUSTERED INDEX IX_QuoteEngagementEvents_QuoteId_CreatedAt
    ON dbo.QuoteEngagementEvents (QuoteId, CreatedAt)
    INCLUDE (EventType, UserId);
-- EventType and UserId are only ever SELECTed here, never filtered or
-- sorted on -- exactly what INCLUDE is for: carry them in the leaf level
-- without making them part of the sort key (which would force a wider,
-- differently-ordered index for no benefit -- the seek only ever needs to
-- be ordered by QuoteId, CreatedAt).

-- Same query, same predicate, one more time.
SELECT EventType, UserId
FROM dbo.QuoteEngagementEvents
WHERE QuoteId = 5
  AND CreatedAt >= '2026-01-15' AND CreatedAt < '2026-01-16';
-- Actual execution plan: Index Seek on
-- IX_QuoteEngagementEvents_QuoteId_CreatedAt ONLY -- the Key Lookup and
-- the Nested Loops that joined it are both gone. One operator, not two --
-- this is the proof the exercise asks for: same query, same predicate,
-- same row count, and the plan shrank by one join.
-- STATISTICS IO (calculated): Scan count 1, logical reads ~3. ~186x fewer
-- reads than the ~559 baseline, and ~75x fewer than the plain-index
-- seek+lookup plan's ~225 -- almost all of that ~225 was the 111 Key
-- Lookups, not the seek itself, which is why removing them (not the seek)
-- is where nearly all of the win comes from.

SET STATISTICS IO OFF;

-- Cleanup, if re-running this file against the same database:
-- DROP INDEX IX_QuoteEngagementEvents_QuoteId_CreatedAt ON dbo.QuoteEngagementEvents;
