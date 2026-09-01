#!/usr/bin/env python3
"""
Day 19 — executable proof of the idempotency defect and its fix.

WHAT THIS IS. A model of the two transaction shapes, run against a real SQLite
database with the same schema the EF migration creates: QuoteAuditEntries as
the handler's side effect, ProcessedMessages with the composite primary key
(MessageId, SubscriptionName) as the dedupe store. Two connections stand in for
two competing consumers.

WHAT THIS IS NOT. It does not execute QuotesApi. It cannot: no .NET SDK is
available in the environment this was written in. It proves that the SHAPE the
code had before the fix loses the guarantee and the shape it has now keeps it —
the database semantics, not the C#. The C# still needs `dotnet test`.

Run:  python3 Day19/verification/idempotency-proof.py
Exit code 0 = every scenario behaved as the fix claims.
"""
import os
import sqlite3
import tempfile

SCHEMA = """
CREATE TABLE QuoteAuditEntries (
    Id       INTEGER PRIMARY KEY AUTOINCREMENT,
    EventId  TEXT    NOT NULL,
    QuoteId  INTEGER NOT NULL
);
CREATE TABLE ProcessedMessages (
    MessageId        TEXT NOT NULL,
    SubscriptionName TEXT NOT NULL,
    ProcessedAtUtc   TEXT NOT NULL,
    Outcome          TEXT NOT NULL,
    PRIMARY KEY (MessageId, SubscriptionName)
);
"""

MESSAGE_ID = "9f2c1b7ae5d34f80a1c6d2e3f4a5b6c7"
SUBSCRIPTION = "audit"

failures = []


def connect(path):
    conn = sqlite3.connect(path, timeout=5, isolation_level=None)
    conn.execute("PRAGMA journal_mode=WAL")
    return conn


def fresh_db():
    path = os.path.join(tempfile.mkdtemp(prefix="day19-"), "quotes.db")
    conn = connect(path)
    conn.executescript(SCHEMA)
    conn.close()
    return path


def has_seen(conn):
    row = conn.execute(
        "SELECT 1 FROM ProcessedMessages WHERE MessageId=? AND SubscriptionName=?",
        (MESSAGE_ID, SUBSCRIPTION),
    ).fetchone()
    return row is not None


def write_audit_row(conn):
    conn.execute(
        "INSERT INTO QuoteAuditEntries (EventId, QuoteId) VALUES (?, ?)",
        (MESSAGE_ID, 999),
    )


def write_dedupe_row(conn):
    conn.execute(
        "INSERT INTO ProcessedMessages VALUES (?, ?, datetime('now'), 'Completed')",
        (MESSAGE_ID, SUBSCRIPTION),
    )


def audit_rows(path):
    conn = connect(path)
    try:
        return conn.execute(
            "SELECT COUNT(*) FROM QuoteAuditEntries WHERE EventId=?", (MESSAGE_ID,)
        ).fetchone()[0]
    finally:
        conn.close()


def report(name, expected, actual, note):
    ok = expected == actual
    if not ok:
        failures.append(name)
    print(f"[{'PASS' if ok else 'FAIL'}] {name}")
    print(f"        expected {expected} audit row(s), got {actual}")
    print(f"        {note}")
    print()


# ----------------------------------------------------------------------
# Scenario 1 — BEFORE the fix, two competing consumers.
# The handler commits its own side effect, then the dedupe row is written
# in a second transaction. Both consumers pass the HasSeen pre-check.
# ----------------------------------------------------------------------
def before_fix_concurrent():
    path = fresh_db()
    a, b = connect(path), connect(path)
    try:
        # Both read "not seen" — this is the check-then-act race.
        assert not has_seen(a) and not has_seen(b)

        # Consumer A: handler's own SaveChangesAsync commits on its own.
        a.execute("BEGIN IMMEDIATE")
        write_audit_row(a)
        a.execute("COMMIT")

        # Consumer B: same, its own transaction, and it commits too.
        b.execute("BEGIN IMMEDIATE")
        write_audit_row(b)
        b.execute("COMMIT")

        # Now each writes the dedupe row in a SECOND transaction.
        a.execute("BEGIN IMMEDIATE")
        write_dedupe_row(a)
        a.execute("COMMIT")

        try:
            b.execute("BEGIN IMMEDIATE")
            write_dedupe_row(b)
            b.execute("COMMIT")
        except sqlite3.IntegrityError:
            # The old code caught this, logged "already processed" and
            # completed the message. Nothing undoes B's audit row: the only
            # thing discarded is the record that the work happened.
            b.execute("ROLLBACK")

        return audit_rows(path)
    finally:
        a.close()
        b.close()


# ----------------------------------------------------------------------
# Scenario 2 — AFTER the fix, same race.
# Side effect and dedupe row share one transaction; the loser rolls back.
# ----------------------------------------------------------------------
def after_fix_concurrent():
    path = fresh_db()
    a, b = connect(path), connect(path)
    try:
        assert not has_seen(a) and not has_seen(b)

        a.execute("BEGIN IMMEDIATE")
        write_audit_row(a)
        write_dedupe_row(a)
        a.execute("COMMIT")

        try:
            b.execute("BEGIN IMMEDIATE")
            write_audit_row(b)
            write_dedupe_row(b)
            b.execute("COMMIT")
        except sqlite3.IntegrityError:
            # The duplicate side effect goes away with the failed dedupe row.
            b.execute("ROLLBACK")

        return audit_rows(path)
    finally:
        a.close()
        b.close()


# ----------------------------------------------------------------------
# Scenario 3 — BEFORE the fix, crash between the two commits, then the
# broker redelivers (at-least-once) and a healthy consumer processes it.
# ----------------------------------------------------------------------
def before_fix_crash_then_redelivery():
    path = fresh_db()
    conn = connect(path)
    try:
        conn.execute("BEGIN IMMEDIATE")
        write_audit_row(conn)
        conn.execute("COMMIT")
        # <-- process dies here: side effect committed, dedupe row never written
        conn.close()

        # Redelivery. The lock expired, so the broker hands the message out
        # again. HasSeen answers "no", because nothing was recorded.
        conn = connect(path)
        if not has_seen(conn):
            conn.execute("BEGIN IMMEDIATE")
            write_audit_row(conn)
            write_dedupe_row(conn)
            conn.execute("COMMIT")

        return audit_rows(path)
    finally:
        conn.close()


# ----------------------------------------------------------------------
# Scenario 4 — AFTER the fix, same crash, same redelivery.
# ----------------------------------------------------------------------
def after_fix_crash_then_redelivery():
    path = fresh_db()
    conn = connect(path)
    try:
        conn.execute("BEGIN IMMEDIATE")
        write_audit_row(conn)
        write_dedupe_row(conn)
        # <-- process dies BEFORE COMMIT: the transaction is never committed,
        #     so SQLite discards it. Nothing happened, atomically.
        conn.close()

        conn = connect(path)
        if not has_seen(conn):
            conn.execute("BEGIN IMMEDIATE")
            write_audit_row(conn)
            write_dedupe_row(conn)
            conn.execute("COMMIT")

        return audit_rows(path)
    finally:
        conn.close()


# ----------------------------------------------------------------------
# Scenario 5 — the composite key. The same message id reaching two
# different subscriptions must be two distinct pieces of work.
# ----------------------------------------------------------------------
def composite_key_keeps_subscriptions_independent():
    path = fresh_db()
    conn = connect(path)
    try:
        conn.execute("BEGIN IMMEDIATE")
        conn.execute(
            "INSERT INTO ProcessedMessages VALUES (?, 'audit', datetime('now'), 'Completed')",
            (MESSAGE_ID,),
        )
        conn.execute("COMMIT")

        # A single-column key would reject this and silently suppress the
        # search-index handler's work.
        conn.execute("BEGIN IMMEDIATE")
        conn.execute(
            "INSERT INTO ProcessedMessages VALUES (?, 'search-index', datetime('now'), 'Completed')",
            (MESSAGE_ID,),
        )
        conn.execute("COMMIT")

        return conn.execute(
            "SELECT COUNT(*) FROM ProcessedMessages WHERE MessageId=?", (MESSAGE_ID,)
        ).fetchone()[0]
    finally:
        conn.close()


print("Day 19 — idempotency, modelled against real SQLite with the migration's schema")
print("=" * 78)
print()

report(
    "BEFORE fix, two competing consumers: duplicate side effect survives",
    2,
    before_fix_concurrent(),
    "two transactions -- the loser's audit row is committed and only its dedupe row is discarded",
)
report(
    "AFTER fix, two competing consumers: exactly one audit row",
    1,
    after_fix_concurrent(),
    "one transaction -- the unique violation rolls the duplicate side effect back",
)
report(
    "BEFORE fix, crash between commits then redelivery: work repeated",
    2,
    before_fix_crash_then_redelivery(),
    "the side effect outlived the record that it happened, so redelivery redid it",
)
report(
    "AFTER fix, crash before commit then redelivery: work done once",
    1,
    after_fix_crash_then_redelivery(),
    "the uncommitted transaction is discarded whole; redelivery does the work exactly once",
)
report(
    "composite key lets one message id be processed once per subscription",
    2,
    composite_key_keeps_subscriptions_independent(),
    "(MessageId, SubscriptionName) rows -- a single-column key would suppress the second",
)

print("=" * 78)
if failures:
    print(f"{len(failures)} scenario(s) did not behave as claimed: {', '.join(failures)}")
    raise SystemExit(1)

print("All 5 scenarios behaved as the fix claims.")
print()
print("Scope: this models the transaction boundaries, not QuotesApi itself.")
print("It does not prove the C# compiles or that the processor wires it this way;")
print("`dotnet test Day7/piece2/QuotesApi.slnx` is still the missing evidence.")
