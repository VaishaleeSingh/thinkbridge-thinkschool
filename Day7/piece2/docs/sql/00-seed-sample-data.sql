-- Day 7 -- sample data for the joins/CTE exercises in this folder.
--
-- Assumes the schema already exists -- created by applying
-- QuotesApi.Migrations.SqlServer against the target database
-- (`dotnet ef database update --project QuotesApi.Migrations.SqlServer`,
-- or letting the app's own startup migration step do it, same as
-- Quotes.Tests.Integration.SqlServer does against its Testcontainers
-- instance). This script only inserts rows, it never creates tables.
--
-- Safe to run once against a fresh/empty database. NOT safe to run twice
-- against the same database without truncating first: Quotes and Users
-- have IDENTITY primary keys and nothing here is upserted against a
-- natural key. That's deliberate -- this is sample data for a query
-- exercise, not an idempotent migration.
--
-- Shape of the data, and why:
--   * Marcus Aurelius gets FIVE quotes, inserted in a single statement so
--     the "most recent" tie-break in 01-author-quote-summary.sql is
--     resolved purely by insertion order (Id), not by anything else
--     coincidentally correlating with it.
--   * Jane Austen, Rumi and Toni Morrison each get exactly ONE quote --
--     the degenerate case: COUNT = 1 and "most recent" = "the only one",
--     which the summary query must not need to special-case.
--   * Three quotes (one each for Marcus Aurelius, Albert Einstein, and
--     Rumi) have CreatedByUserId = NULL -- a real, documented state in
--     this schema (see QuotesApi/Models/Quote.cs's comment on
--     CreatedByUserId): either a legacy quote from before that column
--     existed, or one created by a caller with no identifiable user id.
--     These rows are what makes the INNER JOIN vs LEFT JOIN comparison
--     in 02-join-practice.sql actually differ instead of coincidentally
--     matching.

INSERT INTO dbo.Users (Email, PasswordHash, CreatedAt) VALUES
    (N'reader.one@example.com', N'not-a-real-hash-1', SYSUTCDATETIME()),
    (N'reader.two@example.com', N'not-a-real-hash-2', SYSUTCDATETIME());
-- Fresh IDENTITY(1,1) sequence -> reader.one gets Id 1, reader.two gets
-- Id 2. The CreatedByUserId values below assume that; if this script runs
-- against a database that already has other Users rows, adjust those
-- literals (or better: look the two new Ids up with SCOPE_IDENTITY()/
-- OUTPUT before inserting the Quotes below).

INSERT INTO dbo.Quotes (Author, Text, CreatedByUserId) VALUES
    (N'Marcus Aurelius', N'You have power over your mind - not outside events. Realize this, and you will find strength.', N'1'),
    (N'Marcus Aurelius', N'The happiness of your life depends upon the quality of your thoughts.', N'1'),
    (N'Marcus Aurelius', N'Waste no more time arguing what a good man should be. Be one.', NULL),
    (N'Marcus Aurelius', N'It is not death that a man should fear, but he should fear never beginning to live.', N'2'),
    (N'Marcus Aurelius', N'The best revenge is to be unlike him who performed the injury.', N'1'), -- highest Id for this author: THE "most recent" row
    (N'Maya Angelou', N'People will forget what you said, but never how you made them feel.', N'2'),
    (N'Maya Angelou', N'There is no greater agony than bearing an untold story inside you.', N'2'),
    (N'Albert Einstein', N'Life is like riding a bicycle. To keep your balance, you must keep moving.', N'1'),
    (N'Albert Einstein', N'Imagination is more important than knowledge.', NULL),
    (N'Albert Einstein', N'Try not to become a man of success, but rather try to become a man of value.', N'2'),
    (N'Jane Austen', N'There is nothing I would not do for those who are really my friends.', N'1'),
    (N'Rumi', N'The wound is the place where the light enters you.', NULL),
    (N'Toni Morrison', N'If you want to fly, you have to give up the things that weigh you down.', N'2');

-- Two collections, owned by the two users above, used only by the CROSS
-- JOIN example in 02-join-practice.sql (paired against every distinct
-- author). Not linked to any Quotes via CollectionItem -- that owned-type
-- table isn't needed for any query in this folder, and seeding it would
-- mean looking up the real IDENTITY values assigned to the Quotes rows
-- above, which adds nothing to what this exercise is demonstrating.
INSERT INTO dbo.Collections (Name, OwnerId) VALUES
    (N'Stoic Favorites', N'1'),
    (N'Morning Motivation', N'2');

-- Expected afterwards: 13 Quotes across 6 distinct authors, 2 Users, 2
-- Collections. Verify with:
--   SELECT COUNT(*) AS QuoteCount, COUNT(DISTINCT Author) AS AuthorCount FROM dbo.Quotes;
