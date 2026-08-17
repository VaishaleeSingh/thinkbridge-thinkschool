-- Day 7 -- required exercise:
--
--   "Build a query that, in one statement, returns each author with
--    their quote count and their most-recent quote -- using a CTE, not a
--    correlated subquery in the SELECT."
--
-- Run 00-seed-sample-data.sql first (against the schema created by
-- QuotesApi.Migrations.SqlServer) to get data worth looking at.
--
-- ---------------------------------------------------------------------
-- On "most-recent": Quotes has no timestamp column.
-- ---------------------------------------------------------------------
-- The Quotes table is `(Id, Author, Text, CreatedByUserId)` -- see
-- QuotesApi/Models/Quote.cs and QuotesApi/Migrations. There is no
-- CreatedAt/CreatedOn column to order by (Day 6, which would plausibly
-- have added one, was skipped in this training programme).
--
-- This query uses Id as the recency signal instead: it is an
-- IDENTITY(1,1) primary key, so a higher Id was always inserted later,
-- on both the SQL Server schema this targets and the SQLite schema the
-- rest of the app runs on day to day. That is a real assumption, not a
-- free equivalence -- it breaks if rows are ever deleted and an Id is
-- reused (this schema never reuses IDENTITY values, so that's not a risk
-- here), and it says nothing about wall-clock recency if quotes were
-- ever bulk-imported out of chronological order. Both are worth flagging
-- explicitly rather than leaving implicit.
--
-- If a real CreatedAt DATETIME2 column existed, exactly two lines change
-- and nothing else about the query's shape does:
--   MAX(Id)                              -> MAX(CreatedAt)
--   ON q.Author = aqc.Author AND q.Id = aqc.MostRecentQuoteId
--                                         -> ON q.Author = aqc.Author AND q.CreatedAt = aqc.MostRecentCreatedAt
-- (plus handling the rare case of two quotes from the same author with
-- an identical CreatedAt, which Id can never produce since it's unique.)
--
-- ---------------------------------------------------------------------
-- Why a CTE and not a correlated subquery
-- ---------------------------------------------------------------------
-- The anti-pattern this exercise is steering away from looks like:
--
--   SELECT DISTINCT
--       q.Author,
--       (SELECT COUNT(*) FROM dbo.Quotes q2 WHERE q2.Author = q.Author) AS QuoteCount,
--       (SELECT TOP 1 q3.Text FROM dbo.Quotes q3
--          WHERE q3.Author = q.Author ORDER BY q3.Id DESC)              AS MostRecentQuoteText
--   FROM dbo.Quotes AS q;
--
-- That runs the COUNT(*) subquery and the TOP 1 subquery once PER ROW of
-- the outer query (SQL Server may optimise some of this, but nothing
-- about the query's shape guarantees it will, and it gets worse, not
-- better, the more author-scoped columns get added to the SELECT list).
-- The CTE below computes the count and the winning Id for every author
-- ONCE, as a single set-based GROUP BY, and only then joins back to
-- Quotes one time to pick up that winning row's Text. One pass to
-- aggregate, one join to enrich -- not N correlated lookups.

WITH AuthorQuoteCounts AS (
    SELECT
        Author,
        COUNT(*) AS QuoteCount,
        MAX(Id)  AS MostRecentQuoteId
    FROM dbo.Quotes
    GROUP BY Author
)
SELECT
    aqc.Author,
    aqc.QuoteCount,
    q.Id   AS MostRecentQuoteId,
    q.Text AS MostRecentQuoteText
FROM AuthorQuoteCounts AS aqc
INNER JOIN dbo.Quotes AS q
    ON q.Author = aqc.Author
   AND q.Id     = aqc.MostRecentQuoteId
ORDER BY aqc.Author;

-- Expected result against 00-seed-sample-data.sql (6 rows, one per
-- distinct author -- see docs/day7-joins-and-ctes-submission.md for the
-- full captured output):
--
--   Author            QuoteCount  MostRecentQuoteId  MostRecentQuoteText
--   Albert Einstein   3           <highest Id among his 3 rows>   'Try not to become a man of success...'
--   Jane Austen       1           <his only row's Id>             'There is nothing I would not do...'
--   Marcus Aurelius   5           <highest Id among his 5 rows>   'The best revenge is to be unlike him...'
--   Maya Angelou      2           <highest Id among his 2 rows>   'There is no greater agony...'
--   Rumi              1           <his only row's Id>             'The wound is the place where the light enters you.'
--   Toni Morrison     1           <his only row's Id>              'If you want to fly...'
--
-- The join back to Quotes is INNER, not LEFT: every row in the CTE was
-- itself built FROM Quotes, so a matching Author + Id pair is guaranteed
-- to exist -- an unmatched row here would mean the CTE's own aggregate
-- disagrees with the table it was computed from, which is a bug in the
-- query, not a real-world "no match" case a LEFT JOIN would need to
-- tolerate.
