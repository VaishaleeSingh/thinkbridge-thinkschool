# Screenshots

Real terminal captures of the Day 20 verification runs. Each frame below was
checked against the code and against the run it depicts.

| File | What it shows |
|---|---|
| `01-committed-not-published.png` | The crash script's steps 1–3. `quote id 25 created (HTTP 201)`, then `/api/outbox/status` reporting `pendingCount: 1` with a real `oldestPendingUtc` and an age of `0.0636028s`. **This is the frame the whole proof rests on** — the quote is committed and the event is provably not published, so what happens after the kill is recovery rather than a publish that arrived on time. |
| `02-after-kill-restart.png` | Steps 4–6 through `PASSED`: the forced kill, the restart with a 2-second poll interval and no other action, and `Sent: 5` / `pendingCount: 0`. |
| `03-tests-green.png` | `total: 48, failed: 0` from `dotnet test Quotes.Tests.Integration`. |
| `04-parked-row-does-not-block-the-batch.png` | `A_parked_row_does_not_hold_up_the_rows_behind_it` passing in `Quotes.Tests.Unit`. |

## Two things a reader should know

**The screenshots and the transcript are different runs.** `01` and `02` show
`Sent: 4` before the kill and `Sent: 5` after, because that run reused a
`quotes.db` holding rows from earlier runs. The transcript in
`../day20-crash-recovery-run.txt` shows `Pending: 1` → `Sent: 1`, because that
run started from a freshly created `OutboxMessages` table. Both passed; the
absolute counts differ because the databases did. The number that matters is the
same in both: exactly one row pending before the kill, zero after, and the
`PASSED` line.

**`04` is a unit-test result, not a live parked row.** It shows the test that
proves a parked row does not block the rows queued behind it. That is real
evidence for the head-of-line-blocking guarantee, and it is not evidence of a
`Failed` row observed in a running system — no run has produced one, because
producing one needs a broker that rejects the send. The file is named for what
it contains rather than for the outcome it does not.

## If a live parked row is wanted later

Run with `-WithServiceBus` against a namespace or emulator that rejects the
send, let the relay burn its five attempts, then capture `/api/outbox/status`
showing a `Failed` row in `parked` with its `LastError`, and the rows after it
still reaching `Sent`. Name it `05-parked-row-live.png`.
