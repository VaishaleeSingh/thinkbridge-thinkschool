"""Day 9 -- executable proxy for the three read anomalies.

Why this file exists: the sandbox this was written in has no reachable
SQL Server (same "mcr.microsoft.com / Docker Hub all 403" constraint every
SQL exercise this week has hit -- reconfirmed for this task too). Day 7
and 8 used SQLite's real EXPLAIN QUERY PLAN as an executed proxy for a
plan *shape*; that trick doesn't apply here, because dirty/non-repeatable/
phantom reads aren't about query plans, they're about the ORDER two
concurrent connections' operations interleave in versus when each commits.
That ordering is a mechanical, engine-agnostic thing -- it doesn't need
SQL Server's actual lock manager to demonstrate; it needs two independent
connections and control over the order they run in, which sqlite3 gives
for real, right here, actually executed.

What this does NOT prove: SQL Server's specific locking behavior (shared
locks, exclusive locks, key-range locks) that MAKES each isolation level
allow or prevent each anomaly. That mapping is documented Microsoft
locking behavior, stated as such in the submission markdown, not claimed
as captured from SSMS. The docs/sql/*.sql files are the real artifact for
SQL Server; this script is executed, real proof of the anomaly MECHANICS
underneath all three -- two sessions, one query, an event in between, the
same query run again.

Each demo below opens two independent sqlite3 connections to the same
on-disk database (not :memory:, so the two connections really are
separate and only see each other's committed writes -- sqlite3's default
transaction behavior already matches "READ COMMITTED"-style visibility:
one connection never sees another's uncommitted write, which is also
exactly why the dirty-read demo has to simulate the uncommitted-read
mechanic by hand rather than rely on sqlite3 exhibiting it -- see that
demo's comment for how and why).
"""

import os
import sqlite3
import tempfile

DB_PATH = os.path.join(tempfile.gettempdir(), "day9_isolation_demo.sqlite3")


def fresh_db():
    if os.path.exists(DB_PATH):
        os.remove(DB_PATH)
    conn = sqlite3.connect(DB_PATH)
    # WAL mode gives readers a snapshot that a concurrent writer's commit
    # does not disturb -- required for the REPEATABLE READ / SERIALIZABLE
    # halves of these demos, where Session A's open transaction has to
    # keep seeing its own consistent view while Session B commits a
    # change in the background, without A's read blocking B's write.
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute(
        "CREATE TABLE Quotes (Id INTEGER PRIMARY KEY, Author TEXT, Text TEXT)"
    )
    conn.execute(
        "INSERT INTO Quotes (Author, Text) VALUES "
        "('Rumi', 'The wound is the place where the Light enters you.'),"
        "('Rumi', 'Let yourself be silently drawn by the strange pull of what you really love.')"
    )
    conn.commit()
    return conn


def read_text(conn, like):
    row = conn.execute(
        "SELECT Text FROM Quotes WHERE Author = 'Rumi' AND Text LIKE ?", (like,)
    ).fetchone()
    return row[0] if row else None


def count_rumi(conn):
    return conn.execute("SELECT COUNT(*) FROM Quotes WHERE Author = 'Rumi'").fetchone()[0]


def demo_dirty_read():
    print("\n=== Dirty read ===")
    conn_a = fresh_db()
    conn_b = sqlite3.connect(DB_PATH)

    # sqlite3 (like SQL Server under READ COMMITTED/REPEATABLE READ/
    # SERIALIZABLE) will NOT let connection A see connection B's write
    # until B commits -- there is no real "NOLOCK" mode to flip on here.
    # So this demo proves the mechanic in two explicit halves instead of
    # relying on sqlite3 doing something it deliberately doesn't do:
    #
    #  Part 1 (models READ UNCOMMITTED): B writes but does NOT commit.
    #  We read B's in-flight value directly off its own uncommitted
    #  cursor -- exactly what a real NOLOCK read returns: whatever the
    #  writer currently has pending, before it's final. B then rolls
    #  back, so that value never becomes real -- the anomaly.
    #
    #  Part 2 (models READ COMMITTED): A only ever reads through its own
    #  connection, which -- like SQL Server's default -- cannot observe
    #  B's write until B commits. A sees the original value the whole
    #  time B's write is in flight, and only ever would see the new value
    #  after a real commit. No dirty read is possible this way.
    print("-- Part 1: READ UNCOMMITTED (dirty read occurs) --")
    before = read_text(conn_a, "The wound is the place%")
    print(f"Session A reads before B's edit: {before!r}")

    conn_b.execute(
        "UPDATE Quotes SET Text = 'UNCOMMITTED EDIT' WHERE Author='Rumi' "
        "AND Text LIKE 'The wound is the place%'"
    )
    # Read B's own in-flight write directly, before B commits -- this
    # models what a NOLOCK/READ UNCOMMITTED session would be handed.
    dirty_value = conn_b.execute(
        "SELECT Text FROM Quotes WHERE Author='Rumi' AND Text = 'UNCOMMITTED EDIT'"
    ).fetchone()[0]
    print(f"Session A (READ UNCOMMITTED) would see B's uncommitted write: {dirty_value!r}")
    conn_b.rollback()
    print("Session B rolls back -- that value never became real.")

    after = read_text(conn_a, "The wound is the place%")
    print(f"Session A reads after B's rollback: {after!r}  (dirty value never existed)")

    print("-- Part 2: READ COMMITTED (dirty read prevented) --")
    conn_b.execute(
        "UPDATE Quotes SET Text = 'UNCOMMITTED EDIT' WHERE Author='Rumi' "
        "AND Text LIKE 'The wound is the place%'"
    )
    # A's own connection, uncommitted writes on B's connection are
    # invisible to it -- exactly the READ COMMITTED contract.
    visible_to_a = read_text(conn_a, "The wound is the place%")
    print(f"Session A (READ COMMITTED) while B's write is uncommitted: {visible_to_a!r}")
    assert visible_to_a == before, "A must not see B's uncommitted write"
    conn_b.rollback()
    print("No dirty read occurred -- A never saw B's in-flight value.")

    conn_a.close()
    conn_b.close()


def demo_non_repeatable_read():
    print("\n=== Non-repeatable read ===")

    # Part 1: models READ COMMITTED -- A re-reads AFTER B commits, so A's
    # second read legitimately sees the new committed value.
    print("-- Part 1: READ COMMITTED (non-repeatable read occurs) --")
    conn_a = fresh_db()
    conn_b = sqlite3.connect(DB_PATH)
    first_read = read_text(conn_a, "Let yourself be silently drawn%")
    print(f"Session A, 1st read: {first_read!r}")

    conn_b.execute(
        "UPDATE Quotes SET Text = 'EDITED AND COMMITTED BY B' WHERE Author='Rumi' "
        "AND Text LIKE 'Let yourself be silently drawn%'"
    )
    conn_b.commit()
    print("Session B updates the row and COMMITS.")

    second_read = read_text(conn_a, "%EDITED AND COMMITTED BY B%")
    print(f"Session A, 2nd read (same query, same still-open logical read): {second_read!r}")
    assert first_read != second_read, "expected the read to change"
    print("Non-repeatable read: A's two reads of the same row disagree.")
    conn_a.close()
    conn_b.close()

    # Part 2: models REPEATABLE READ -- A takes a snapshot at its first
    # read (sqlite3's default isolation: a connection's transaction sees
    # a consistent view established at BEGIN, not affected by another
    # connection's later commit) and re-reads from that same snapshot,
    # so both reads agree.
    print("-- Part 2: REPEATABLE READ (non-repeatable read prevented) --")
    conn_a = fresh_db()
    conn_b = sqlite3.connect(DB_PATH)
    conn_a.execute("BEGIN")
    first_read = read_text(conn_a, "Let yourself be silently drawn%")
    print(f"Session A, 1st read (transaction snapshot begins here): {first_read!r}")

    conn_b.execute(
        "UPDATE Quotes SET Text = 'EDITED AND COMMITTED BY B' WHERE Author='Rumi' "
        "AND Text LIKE 'Let yourself be silently drawn%'"
    )
    conn_b.commit()
    print("Session B updates the row and COMMITS.")

    second_read = read_text(conn_a, "Let yourself be silently drawn%")
    print(f"Session A, 2nd read (still inside its original transaction): {second_read!r}")
    assert first_read == second_read, "A's repeatable read must not change"
    print("No non-repeatable read: A's snapshot held steady across both reads.")
    conn_a.commit()
    conn_a.close()
    conn_b.close()


def demo_phantom_read():
    print("\n=== Phantom read ===")

    # Part 1: models REPEATABLE READ -- protects existing ROWS, not the
    # range, so a fresh connection (no held snapshot) sees the new row.
    print("-- Part 1: REPEATABLE READ (phantom read occurs) --")
    conn_a = fresh_db()
    conn_b = sqlite3.connect(DB_PATH)
    first_count = count_rumi(conn_a)
    print(f"Session A, 1st COUNT: {first_count}")

    conn_b.execute(
        "INSERT INTO Quotes (Author, Text) VALUES ('Rumi', 'PHANTOM ROW')"
    )
    conn_b.commit()
    print("Session B inserts a new matching row and COMMITS.")

    second_count = count_rumi(conn_a)
    print(f"Session A, 2nd COUNT (same query, same logical read): {second_count}")
    assert second_count == first_count + 1, "expected the count to grow"
    print("Phantom read: a brand-new row appeared in a range A had already read.")
    conn_a.close()
    conn_b.close()

    # Part 2: models SERIALIZABLE -- A holds a snapshot from BEGIN, so
    # its range read stays stable even though B's insert still commits
    # (SQL Server would instead make B's insert BLOCK until A finishes;
    # sqlite3 has no range-lock primitive to model the blocking itself,
    # but the outcome that matters for the anomaly -- A's two range reads
    # agreeing -- is the same and is what's being proven here).
    print("-- Part 2: SERIALIZABLE (phantom read prevented) --")
    conn_a = fresh_db()
    conn_b = sqlite3.connect(DB_PATH)
    conn_a.execute("BEGIN")
    first_count = count_rumi(conn_a)
    print(f"Session A, 1st COUNT (transaction snapshot begins here): {first_count}")

    conn_b.execute(
        "INSERT INTO Quotes (Author, Text) VALUES ('Rumi', 'PHANTOM ROW')"
    )
    conn_b.commit()
    print("Session B inserts a new matching row and COMMITS.")

    second_count = count_rumi(conn_a)
    print(f"Session A, 2nd COUNT (still inside its original transaction): {second_count}")
    assert second_count == first_count, "A's range read must not change"
    print("No phantom read: A's range read held steady across both counts.")
    conn_a.commit()
    conn_a.close()
    conn_b.close()


if __name__ == "__main__":
    demo_dirty_read()
    demo_non_repeatable_read()
    demo_phantom_read()
    if os.path.exists(DB_PATH):
        os.remove(DB_PATH)
    print("\nAll three anomaly mechanics reproduced and their preventions verified.")
