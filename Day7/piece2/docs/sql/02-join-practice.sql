-- Day 7 -- join-type fluency practice (supporting material; the graded
-- deliverable is 01-author-quote-summary.sql). Run 00-seed-sample-data.sql
-- first.
--
-- All three queries join on the same real asymmetry already present in
-- the schema: Quotes.CreatedByUserId is a string user id copied from a
-- JWT `sub` or Entra `oid` claim at creation time (see
-- QuotesApi/Models/Quote.cs), not a foreign key. Two things make a row's
-- CreatedByUserId fail to match a Users row, both real and already
-- present in 00-seed-sample-data.sql's data:
--   * it's NULL -- a legacy quote, or one created by a caller with no
--     identifiable user id;
--   * it identifies an Entra ID caller, who authenticates against Azure
--     directly and never gets a row in the local Users table at all
--     (Users is only for this app's own first-party JWT login).
-- That's why INNER JOIN and LEFT JOIN genuinely return different row
-- counts below, instead of only differing in theory.

-- -----------------------------------------------------------------------
-- 1) INNER JOIN -- quotes created by a locally-registered user only.
-- -----------------------------------------------------------------------
-- Answers: "which quotes can I attribute to a real Users row, with an
-- email address I can show someone?" Rows with a NULL or unmatched
-- CreatedByUserId are correctly excluded -- there is nothing to attribute
-- them to.
SELECT
    q.Id,
    q.Author,
    u.Email AS CreatedByEmail
FROM dbo.Quotes AS q
INNER JOIN dbo.Users AS u
    ON u.Id = TRY_CAST(q.CreatedByUserId AS int)
ORDER BY q.Id;
-- Expected: 10 of the 13 seeded quotes (the 3 with NULL CreatedByUserId
-- are dropped).

-- -----------------------------------------------------------------------
-- 2) LEFT JOIN -- every quote, whether or not it can be attributed.
-- -----------------------------------------------------------------------
-- Answers: "show me every quote, and the owner's email where we happen to
-- have one." This is the query you'd actually want for an admin listing
-- screen -- an INNER JOIN there would silently make legacy and Entra-
-- authored quotes disappear from the list entirely, which is a real bug
-- class (a LEFT JOIN accidentally written or rewritten as an INNER JOIN
-- during a later "simplification" is one of the most common ways a report
-- silently starts dropping rows).
SELECT
    q.Id,
    q.Author,
    q.CreatedByUserId,
    u.Email AS CreatedByEmail  -- NULL where there's no matching Users row
FROM dbo.Quotes AS q
LEFT JOIN dbo.Users AS u
    ON u.Id = TRY_CAST(q.CreatedByUserId AS int)
ORDER BY q.Id;
-- Expected: all 13 seeded quotes, CreatedByEmail NULL on exactly the 3
-- rows the INNER JOIN above dropped.

-- -----------------------------------------------------------------------
-- 3) CROSS JOIN -- deliberately contrived, and labelled as such.
-- -----------------------------------------------------------------------
-- Cross joins are rare in real reporting because pairing every row of one
-- table with every row of another is almost never the question being
-- asked -- it's usually the wrong join used by accident (an ON clause
-- forgotten or wrong), not a query someone reaches for on purpose. The one
-- legitimate use worth knowing: building a dense grid that every
-- combination of two dimensions is guaranteed to appear in, so a later
-- LEFT JOIN against real activity can show explicit zeros instead of
-- missing rows (the same idea 03-recursive-cte-practice.sql's date series
-- exists for).
--
-- Here: every Collection paired with every distinct Author -- the
-- starting grid for "which of my collections has NOT yet got a quote by
-- this author", which a LEFT JOIN from this grid against CollectionItem
-- (joined through to Quotes.Author) would answer by filtering to NULL.
SELECT
    c.Name AS CollectionName,
    a.Author
FROM dbo.Collections AS c
CROSS JOIN (SELECT DISTINCT Author FROM dbo.Quotes) AS a
ORDER BY c.Name, a.Author;
-- Expected: 2 collections x 6 distinct authors = 12 rows, no ON clause
-- because a cross join has none -- every combination is included by
-- definition.
