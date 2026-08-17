-- Day 7 -- set operations from a spec: UNION / INTERSECT / EXCEPT.
--
-- IMPORTANT CAVEAT, stated up front: the real schema has no tagging
-- concept at all -- `Quotes` is (Id, Author, Text, CreatedByUserId), no
-- `Tags` table, no `QuoteTags` junction, no 'classic'/'modern' sets. This
-- is a bigger gap than the previous two Day 7 exercises (which only
-- needed a missing `CreatedAt` column) -- there is nothing to select FROM
-- for "authors with quotes but no tags" as things stand.
--
-- Rather than skip the exercise or answer only in prose, the temp tables
-- below supply an ILLUSTRATIVE Tags/QuoteTags model -- a `Tags(TagId,
-- TagName, Category)` table and a many-to-many `QuoteTags(QuoteId,
-- TagId)` junction, the obvious real shape this would take -- joined
-- against the real `Quotes` rows from 00-seed-sample-data.sql by Id. This
-- produces real, runnable T-SQL and real result rows, not just a
-- description of the pattern. If a real tagging feature gets built, these
-- two tables are the actual design to add (as real EF entities + a
-- migration); every query below becomes a straight SELECT against them
-- with the temp-table setup deleted -- nothing else changes.
--
-- Temp tables, not a shared CTE: a WITH clause only scopes to the ONE
-- statement immediately following it, and this file runs three separate
-- EXCEPT / INTERSECT / UNION statements against the same illustrative
-- data -- a CTE defined before query 1 would already be out of scope by
-- query 2. #Tags / #QuoteTags persist for the rest of the batch instead.
--
-- Tag assignments were chosen deliberately so all three set operations
-- return a real, non-trivial answer instead of an empty or all-rows
-- result:
--   Marcus Aurelius -> classic only (Stoicism, Ancient Wisdom)
--   Maya Angelou    -> modern only (Empowerment, Self-Help)
--   Albert Einstein -> BOTH classic (Literature) and modern (Self-Help)
--   Jane Austen     -> classic only (Literature)
--   Rumi            -> classic only (Stoicism)
--   Toni Morrison   -> no tagged quotes at all

SELECT * INTO #Tags FROM (VALUES
    (1, N'Stoicism',       N'classic'),
    (2, N'Ancient Wisdom', N'classic'),
    (3, N'Empowerment',    N'modern'),
    (4, N'Self-Help',      N'modern'),
    (5, N'Literature',     N'classic')
) AS t(TagId, TagName, Category);

-- (QuoteId, TagId) -- Id values match 00-seed-sample-data.sql's Quotes
-- rows (Marcus Aurelius = 1-5, Maya Angelou = 6-7, Albert Einstein =
-- 8-10, Jane Austen = 11, Rumi = 12, Toni Morrison = 13 -- and Toni
-- Morrison deliberately gets no row here at all).
SELECT * INTO #QuoteTags FROM (VALUES
    (1, 1), (2, 2),   -- Marcus Aurelius: classic
    (6, 3), (7, 4),   -- Maya Angelou: modern
    (8, 5), (9, 4),   -- Albert Einstein: classic AND modern
    (11, 5),          -- Jane Austen: classic
    (12, 1)           -- Rumi: classic
) AS qt(QuoteId, TagId);

-- =======================================================================
-- 1) Authors with quotes but no tags -- EXCEPT.
-- =======================================================================
-- "Every author who has written a quote" minus "every author who has AT
-- LEAST ONE tagged quote". EXCEPT is the right tool specifically because
-- the question is phrased as a subtraction ("has quotes BUT no tags") --
-- a NOT IN / NOT EXISTS anti-join would answer the same question, but
-- EXCEPT states the set-subtraction intent directly, matching how the
-- business question was actually asked.
SELECT Author FROM dbo.Quotes

EXCEPT

SELECT q.Author
FROM dbo.Quotes AS q
INNER JOIN #QuoteTags AS qt ON qt.QuoteId = q.Id
ORDER BY Author;
-- Expected: Toni Morrison only.

-- =======================================================================
-- 2) Authors in both the 'classic' and 'modern' sets -- INTERSECT.
-- =======================================================================
-- Two independent sets of authors -- those with at least one
-- classic-tagged quote, and those with at least one modern-tagged quote
-- -- and the question asks for membership in BOTH. INTERSECT states that
-- directly; an INNER JOIN-and-dedupe version exists but buries the actual
-- question (set membership on both sides) inside join conditions instead
-- of naming it.
SELECT q.Author
FROM dbo.Quotes AS q
INNER JOIN #QuoteTags AS qt ON qt.QuoteId = q.Id
INNER JOIN #Tags      AS t  ON t.TagId    = qt.TagId
WHERE t.Category = N'classic'

INTERSECT

SELECT q.Author
FROM dbo.Quotes AS q
INNER JOIN #QuoteTags AS qt ON qt.QuoteId = q.Id
INNER JOIN #Tags      AS t  ON t.TagId    = qt.TagId
WHERE t.Category = N'modern'
ORDER BY Author;
-- Expected: Albert Einstein only.

-- =======================================================================
-- 3) The combined distinct tag list across the two categories -- UNION.
-- =======================================================================
-- Not UNION ALL: "Stoicism" is applied to two different classic quotes
-- (Marcus Aurelius's and Rumi's), so the classic branch alone returns it
-- twice before the set operation runs -- UNION's implicit dedup is doing
-- real work here, not a no-op. Run the classic branch alone against this
-- same data and "Stoicism" appears twice; that's the concrete case for
-- UNION over UNION ALL, not just the textbook rule.
SELECT t.TagName
FROM #QuoteTags AS qt
INNER JOIN #Tags AS t ON t.TagId = qt.TagId
WHERE t.Category = N'classic'

UNION

SELECT t.TagName
FROM #QuoteTags AS qt
INNER JOIN #Tags AS t ON t.TagId = qt.TagId
WHERE t.Category = N'modern'
ORDER BY TagName;
-- Expected: 5 distinct tags -- Ancient Wisdom, Empowerment, Literature,
-- Self-Help, Stoicism.

DROP TABLE #QuoteTags;
DROP TABLE #Tags;
