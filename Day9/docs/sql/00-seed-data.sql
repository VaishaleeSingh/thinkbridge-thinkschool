-- Day 9 -- isolation levels + read anomalies: shared seed data.
--
-- Reuses dbo.Quotes exactly as it already exists in this app's schema --
-- no new table, no schema change. Only new content is a small, known set
-- of rows by a single author ("Rumi") so that every demo below has a
-- deterministic starting point: an exact row to update (for the dirty-read
-- and non-repeatable-read demos) and an exact row COUNT to grow (for the
-- phantom-read demo).
--
-- Idempotent: safe to re-run. Uses a NOT EXISTS guard rather than
-- TRUNCATE/DELETE, because this table is shared with every other day's
-- exercise and the running application -- this script must only ever add
-- rows, never remove or reset anything that isn't its own seed data.

IF NOT EXISTS (SELECT 1 FROM dbo.Quotes WHERE Author = 'Rumi' AND Text = 'The wound is the place where the Light enters you.')
BEGIN
    INSERT INTO dbo.Quotes (Author, Text, CreatedByUserId)
    VALUES ('Rumi', 'The wound is the place where the Light enters you.', NULL);
END;

IF NOT EXISTS (SELECT 1 FROM dbo.Quotes WHERE Author = 'Rumi' AND Text = 'Let yourself be silently drawn by the strange pull of what you really love.')
BEGIN
    INSERT INTO dbo.Quotes (Author, Text, CreatedByUserId)
    VALUES ('Rumi', 'Let yourself be silently drawn by the strange pull of what you really love.', NULL);
END;

-- Baseline check every demo below assumes: exactly 2 Rumi quotes before
-- Session B does anything.
SELECT COUNT(*) AS RumiQuoteCount FROM dbo.Quotes WHERE Author = 'Rumi';

-- Cleanup, if this needs to be removed after the exercise:
-- DELETE FROM dbo.Quotes WHERE Author = 'Rumi';
