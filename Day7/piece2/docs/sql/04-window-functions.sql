-- Day 7 -- window functions.
--
-- "Window functions are the difference between a junior and a senior SQL
-- author." Four techniques below, each against a real question this
-- schema can actually answer -- not a toy table. Run
-- docs/sql/00-seed-sample-data.sql first (same seed data the joins/CTE
-- task used -- reused as-is here, not duplicated).

-- =======================================================================
-- 1) ROW_NUMBER -- rewrite 01-author-quote-summary.sql's own query.
-- =======================================================================
-- That query solved "each author's quote count + most-recent quote" with
-- a CTE that aggregates once (GROUP BY), then JOINs back to Quotes to
-- pick up the winning row's Text -- two passes over the data, joined
-- together.
--
-- A window function does it in ONE pass, with no join back, because
-- COUNT(*) OVER (PARTITION BY Author) adds a column instead of collapsing
-- rows -- the aggregate and the row-level detail (Text) coexist on the
-- same row. That's the actual mechanical difference a window function
-- buys you over a GROUP BY: an aggregate function turns N rows into 1;
-- a window function keeps all N rows and decorates each one.
--
-- One thing this does NOT buy you: T-SQL has no QUALIFY clause, so
-- filtering a window function down to one row per group still needs a
-- wrapping CTE or derived table -- "senior" here isn't "no CTE at all",
-- it's "the CTE stops needing a join back to the base table".
WITH Ranked AS (
    SELECT
        Author,
        Id,
        Text,
        ROW_NUMBER() OVER (PARTITION BY Author ORDER BY Id DESC) AS rn,
        COUNT(*)     OVER (PARTITION BY Author)                  AS QuoteCount
    FROM dbo.Quotes
)
SELECT
    Author,
    QuoteCount,
    Id   AS MostRecentQuoteId,
    Text AS MostRecentQuoteText
FROM Ranked
WHERE rn = 1
ORDER BY Author;
-- Expected: the SAME 6 rows as 01-author-quote-summary.sql, exactly --
-- verified by direct comparison in docs/day7-window-functions-submission.md,
-- not just asserted to be equivalent.

-- =======================================================================
-- 2) RANK vs DENSE_RANK -- an authors-by-quote-count leaderboard.
-- =======================================================================
-- Ordered ASC (fewest quotes first) rather than the more "natural" DESC
-- leaderboard order, deliberately: this seed data's only tie (Jane
-- Austen, Rumi, Toni Morrison, all at 1 quote) sits at the low end.
-- Ordering DESC puts that tie LAST, where neither function's "skip"
-- behaviour has a following row to show up against -- both would just
-- stop. Ordering ASC puts the tie FIRST, followed by Maya Angelou's
-- distinct count of 2 -- which is exactly where RANK and DENSE_RANK
-- diverge: RANK jumps to 4 (three rows precede her), DENSE_RANK goes to 2
-- (the next distinct value). That divergence is the entire reason these
-- are two different functions, so the query is built to actually
-- demonstrate it against real data rather than only describe it in a
-- comment.
WITH AuthorCounts AS (
    SELECT Author, COUNT(*) AS QuoteCount
    FROM dbo.Quotes
    GROUP BY Author
)
SELECT
    Author,
    QuoteCount,
    RANK()       OVER (ORDER BY QuoteCount ASC) AS QuoteRank,
    DENSE_RANK() OVER (ORDER BY QuoteCount ASC) AS QuoteDenseRank
FROM AuthorCounts
ORDER BY QuoteCount ASC, Author;
-- Expected: Jane Austen / Rumi / Toni Morrison all (1, 1, 1); Maya
-- Angelou (2, 4, 2) -- the divergence; Albert Einstein (3, 5, 3); Marcus
-- Aurelius (5, 6, 4).

-- =======================================================================
-- 3) LAG / LEAD -- each quote alongside the same author's neighbours.
-- =======================================================================
-- "What did this author say right before/after this one" -- partitioned
-- by Author so LAG/LEAD never leaks across authors, ordered by Id (the
-- same documented recency proxy 01-author-quote-summary.sql uses, for
-- the same reason: no CreatedAt column exists).
--
-- The three single-quote authors (Jane Austen, Rumi, Toni Morrison) --
-- the same degenerate case 00-seed-sample-data.sql was built to exercise
-- for the joins task -- correctly get NULL on BOTH sides here: there is
-- no previous or next row within their own partition. That's not a bug
-- to special-case, it's LAG/LEAD's correct, boring behaviour at a
-- partition boundary.
SELECT
    Author,
    Id,
    Text,
    LAG(Text)  OVER (PARTITION BY Author ORDER BY Id) AS PreviousQuoteBySameAuthor,
    LEAD(Text) OVER (PARTITION BY Author ORDER BY Id) AS NextQuoteBySameAuthor
FROM dbo.Quotes
ORDER BY Author, Id;
-- Expected: 13 rows; PreviousQuoteBySameAuthor and NextQuoteBySameAuthor
-- both NULL for Jane Austen, Rumi and Toni Morrison; Marcus Aurelius's
-- 5-quote chain has a NULL only at its first row's Previous and its last
-- row's Next, nowhere in between.

-- =======================================================================
-- 4) Running total -- SUM() OVER (ORDER BY ...), global and per-author.
-- =======================================================================
-- Both running totals in the same query, over the same ORDER BY Id, so
-- the effect of adding PARTITION BY to an otherwise-identical running
-- total is directly visible side by side rather than argued about in
-- prose: RunningTotalAllQuotes only ever goes up; RunningTotalForThisAuthor
-- resets to 1 every time the Author changes.
--
-- ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW is stated explicitly
-- rather than left as the implicit default. Id is unique here, so the
-- default RANGE frame happens to behave identically to ROWS -- but that's
-- a coincidence of this column having no ties, not something to rely on.
-- Stating the frame explicitly is the correct habit regardless.
SELECT
    Id,
    Author,
    SUM(1) OVER (
        ORDER BY Id
        ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
    ) AS RunningTotalAllQuotes,
    SUM(1) OVER (
        PARTITION BY Author
        ORDER BY Id
        ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
    ) AS RunningTotalForThisAuthor
FROM dbo.Quotes
ORDER BY Id;
-- Expected: RunningTotalAllQuotes reaches 13 on the last row (Id order);
-- RunningTotalForThisAuthor restarts at 1 on each author's first row by
-- Id and reaches that author's QuoteCount on their last.
