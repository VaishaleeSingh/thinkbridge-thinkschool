-- Day 9 -- capture the real deadlock graph after 01-deadlock-reproduction.sql
-- has run and produced a real 1205 deadlock.
--
-- On-prem SQL Server would normally use DBCC TRACEON(1222) to write the
-- deadlock graph to the error log, or a dedicated Extended Events session
-- with an event_file target. Azure SQL Database (PaaS) does not support
-- server-level trace flags at all -- DBCC TRACEON is rejected outright on
-- a managed database, there is no instance to set a global trace flag on.
--
-- The real, working equivalent on Azure SQL Database: the built-in
-- "system_health" Extended Events session runs by default on every Azure
-- SQL Database and already captures xml_deadlock_report automatically,
-- with no setup required. Query its in-memory ring buffer target directly:

SELECT
    CAST(xet.target_data AS XML) AS RingBufferXml
FROM sys.dm_xe_database_session_targets AS xet
JOIN sys.dm_xe_database_sessions AS xe
    ON xe.address = xet.event_session_address
WHERE xe.name = 'system_health'
  AND xet.target_name = 'ring_buffer';

-- The result is one XML column holding every recent event system_health
-- captured, deadlock reports included. To pull out just the deadlock
-- graphs as their own rows:

;WITH RingBuffer AS (
    SELECT CAST(xet.target_data AS XML) AS RingBufferXml
    FROM sys.dm_xe_database_session_targets AS xet
    JOIN sys.dm_xe_database_sessions AS xe
        ON xe.address = xet.event_session_address
    WHERE xe.name = 'system_health'
      AND xet.target_name = 'ring_buffer'
)
SELECT
    event_xml.value('(@timestamp)[1]', 'DATETIME2') AS EventTimestamp,
    event_xml.query('.')                            AS DeadlockGraphXml
FROM RingBuffer
CROSS APPLY RingBufferXml.nodes('RingBufferTarget/event[@name="xml_deadlock_report"]') AS Events(event_xml)
ORDER BY EventTimestamp DESC;

-- Each DeadlockGraphXml row is a real <deadlock> XML document containing
-- <process-list> (each session's SPID, the query it was running, its
-- wait type) and <resource-list> (the exact key/row each side held and
-- wanted) -- the same structural information DBCC TRACEON(1222) or SSMS's
-- "deadlock graph" tab would show, just reached through Azure SQL
-- Database's supported path instead of an unsupported trace flag.

-- =======================================================================
-- REAL AZURE SQL DATABASE RESULT -- this approach does NOT work here,
-- and two fallbacks don't either. Tested live, not assumed.
-- =======================================================================
-- Everything above this line describes the commonly-documented approach.
-- Run against the real quotesdb (thinkschool-quotes-sql) right after the
-- real deadlock in 01-deadlock-reproduction.sql, all three attempts
-- failed, each for a different, specific reason:
--
-- Attempt 1 -- query the system_health ring buffer (the SQL above):
--   SELECT COUNT(*) FROM sys.dm_xe_database_sessions;
--   -- Result: 0 rows.
--   SELECT * FROM sys.database_event_sessions;
--   -- Result: 0 rows.
--   There is no "system_health" Extended Events session running on this
--   Azure SQL Database at all -- contrary to Microsoft's own general
--   documentation that system_health runs by default on every Azure SQL
--   Database, this specific database (Free-tier quotesdb) has none.
--   The two queries above return zero rows, not an error -- so the
--   failure is silent unless you think to check session existence first.
--
-- Attempt 2 -- create a dedicated Extended Events session instead of
-- relying on system_health:
--   CREATE EVENT SESSION CaptureDeadlocks ON DATABASE
--   ADD EVENT sqlserver.xml_deadlock_report
--   ADD TARGET package0.ring_buffer;
--   -- Result: rejected outright with:
--   --   "The event 'sqlserver.xml_deadlock_report' is not available for
--   --    Azure SQL Database."
--   This event simply isn't in Azure SQL Database's Extended Events
--   catalog for a database-scoped session, regardless of session name --
--   this isn't a permissions or existing-session problem, the event
--   itself is unavailable on this platform.
--
-- Attempt 3 -- sys.event_log, a documented alternative some SQL Server
-- versions expose for deadlock/error events:
--   SELECT * FROM sys.event_log(DB_NAME());
--   -- Result: "Invalid object name 'sys.event_log'."
--   This function does not exist on Azure SQL Database at all.
--
-- Also checked outside T-SQL: the Azure Portal's "Query performance
-- insight" blade for this database -- at the time of testing it showed
-- "At this time, there is no performance data available" (Query Store
-- had not yet accumulated enough history), so there was no UI path to a
-- deadlock graph either.
--
-- Conclusion, stated honestly rather than worked around: on this Azure
-- SQL Database (Free-tier quotesdb), there is currently no method --
-- trace flag, system_health, a custom XE session, sys.event_log, or the
-- portal's performance-insight UI -- that surfaces an actual deadlock
-- XML graph. The deadlock itself is real (Msg 1205, captured in
-- 01-deadlock-reproduction.sql's appendix) and its cause is fully
-- diagnosable from the reproduction script and the two sessions' known
-- lock order; what's missing is only the machine-generated <deadlock>
-- XML artifact, not the ability to explain what happened. A client with
-- direct engine access (SSMS, Azure Data Studio, or sqlcmd connected to
-- this same database) would very likely close this gap, since
-- system_health and xml_deadlock_report are standard on most Azure SQL
-- Database tiers -- this appears to be specific to this database/tier's
-- current configuration, not a general Azure SQL Database limitation.
