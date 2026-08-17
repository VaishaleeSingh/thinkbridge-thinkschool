-- Day 7 -- per-author running count + gap in days since the previous
-- quote (LAG).
--
-- IMPORTANT CAVEAT, stated up front rather than discovered halfway
-- through: the real Quotes table is (Id, Author, Text, CreatedByUserId)
-- -- no CreatedAt column exists yet (the same gap 01-author-quote-
-- summary.sql and 04-window-functions.sql already documented and worked
-- around with Id as an ordering proxy). "Gap in DAYS" specifically needs
-- a real timestamp to mean anything -- Id has no time unit, so unlike the
-- earlier two exercises this one can't be honestly answered with Id
-- alone.
--
-- Rather than a schema migration (out of scope for a query exercise, and
-- the last two submissions already flagged this as the follow-up once
-- CreatedAt exists), the CTE below supplies ILLUSTRATIVE CreatedAt values
-- inline, spread over July-August 2026, so the query is real, runnable
-- T-SQL producing real sample rows -- not just a description of what it
-- would do. The moment a real CreatedAt DATETIME2 column exists on
-- Quotes, delete the VALUES CTE and select FROM dbo.Quotes directly;
-- nothing else about the query changes.

WITH QuotesWithIllustrativeDates AS (
    SELECT * FROM (VALUES
        (1,  N'Marcus Aurelius',  N'You have power over your mind - not outside events. Realize this, and you will find strength.', CAST('2026-07-01' AS date)),
        (2,  N'Marcus Aurelius',  N'The happiness of your life depends upon the quality of your thoughts.', CAST('2026-07-04' AS date)),
        (3,  N'Marcus Aurelius',  N'Waste no more time arguing what a good man should be. Be one.', CAST('2026-07-10' AS date)),
        (4,  N'Marcus Aurelius',  N'It is not death that a man should fear, but he should fear never beginning to live.', CAST('2026-07-11' AS date)),
        (5,  N'Marcus Aurelius',  N'The best revenge is to be unlike him who performed the injury.', CAST('2026-07-25' AS date)),
        (6,  N'Maya Angelou',     N'People will forget what you said, but never how you made them feel.', CAST('2026-07-02' AS date)),
        (7,  N'Maya Angelou',     N'There is no greater agony than bearing an untold story inside you.', CAST('2026-07-20' AS date)),
        (8,  N'Albert Einstein',  N'Life is like riding a bicycle. To keep your balance, you must keep moving.', CAST('2026-07-05' AS date)),
        (9,  N'Albert Einstein',  N'Imagination is more important than knowledge.', CAST('2026-07-06' AS date)),
        (10, N'Albert Einstein',  N'Try not to become a man of success, but rather try to become a man of value.', CAST('2026-07-30' AS date)),
        (11, N'Jane Austen',      N'There is nothing I would not do for those who are really my friends.', CAST('2026-07-15' AS date)),
        (12, N'Rumi',             N'The wound is the place where the light enters you.', CAST('2026-07-22' AS date)),
        (13, N'Toni Morrison',    N'If you want to fly, you have to give up the things that weigh you down.', CAST('2026-08-01' AS date))
    ) AS v(Id, Author, Text, CreatedAt)
)
SELECT
    Author,
    Id,
    Text,
    CreatedAt,
    COUNT(*) OVER (
        PARTITION BY Author ORDER BY CreatedAt
        ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
    ) AS RunningQuoteCount,
    DATEDIFF(
        DAY,
        LAG(CreatedAt) OVER (PARTITION BY Author ORDER BY CreatedAt),
        CreatedAt
    ) AS GapDaysSincePreviousQuote
FROM QuotesWithIllustrativeDates
ORDER BY Author, CreatedAt;

-- Expected (verified by real execution -- see
-- docs/day7-window-functions-submission.md for the full table): each
-- author's first quote by date has GapDaysSincePreviousQuote = NULL (no
-- previous row in that partition, LAG's correct boundary behaviour --
-- the same NULL-at-the-edge case 04-window-functions.sql's LAG/LEAD
-- query already exercises), RunningQuoteCount = 1 there and climbing by
-- 1 per subsequent quote from the same author, and every later row's
-- GapDaysSincePreviousQuote is a real day count (e.g. Marcus Aurelius:
-- NULL, 3, 6, 1, 14).
