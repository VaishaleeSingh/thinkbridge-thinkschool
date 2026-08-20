# Day 10 — mentor submission (EF Core change tracker + AsNoTracking())

## GitHub link

https://github.com/thinkbridge-thinkschool/VaishaleeSingh/tree/day10-efcore-change-tracker-asnotracking/Day10

(Replace with the pull request URL once opened.)

## What this task actually asks for, in simple words

EF Core's `DbContext` doesn't just run your queries — every time it loads
an entity, it quietly keeps a private notebook (the **change tracker**)
recording "I have a `Quote` with `Id = 42`, and here is exactly what its
properties looked like when I loaded it." That notebook is what makes
`SaveChanges()` work without you ever writing an `UPDATE` statement: you
mutate a property on an object you got back from a query, call
`SaveChanges()`, and EF diffs the object against its own notebook entry to
figure out what changed and generates the `UPDATE` for you.

Three things about that notebook are easy to not notice until they cause a
real bug:

1. **Identity resolution** — if you ask the same `DbContext` for row
   `Id = 42` twice, you don't get two separate objects that happen to have
   equal data — you get the exact same object, both times. EF checks its
   notebook first; if it already has an entry for that key, it hands back
   the existing tracked instance instead of materializing a new one.
2. **Tracked vs. not tracked** — a plain `db.Quotes.Where(...)` result is
   tracked automatically. `db.Quotes.AsNoTracking().Where(...)` explicitly
   tells EF "don't write an entry in the notebook for this." The bug this
   causes in real code: edit a property on an `AsNoTracking()` entity and
   call `SaveChanges()` — nothing happens. No exception, no warning, the
   code compiles and runs fine — it just silently doesn't persist, because
   the tracker never knew that object existed in the first place.
3. **The read-path win** — keeping that notebook has a real cost: for
   every tracked row, EF has to snapshot its state (so it can later detect
   what changed) and register it in an internal dictionary keyed by
   primary key (so identity resolution in point 1 works). For a query
   that's purely read-only — an API endpoint returning data to a client
   that will never call `SaveChanges()` on it — none of that bookkeeping
   is needed, and `AsNoTracking()` skips it: less time spent per row, and
   less memory allocated per row, because there's no tracked snapshot to
   allocate.

This task asks for real, working proof of all three — not a description
of how EF Core is documented to behave, but actually running code that
shows it — plus a real measurement of point 3 on a 10,000-row read.

## Implementation plan (written before writing any code)

1. **A minimal, self-contained EF Core project** — its own tiny `Quote`
   model and `DbContext`, on SQLite rather than a dependency on
   `QuotesApi`'s SQL Server setup or its auth/migrations. The change
   tracker's behavior is provider-agnostic: what's demonstrated here is
   true of `QuotesApi`'s real `Quote`/`QuotesDbContext` on Azure SQL
   Database exactly the same way, because identity resolution, tracked
   vs. no-tracking, and the tracking overhead all live in EF Core's
   provider-independent core, not in the SQL Server or SQLite provider.
   SQLite was chosen over EF Core's `InMemory` provider deliberately:
   `InMemory` doesn't generate or execute real SQL, so it would risk
   demonstrating an artifact of the fake provider rather than the real
   query pipeline AsNoTracking() actually changes.
2. **Part 1 — identity resolution.** Query the same row twice inside one
   `DbContext`, prove `ReferenceEquals` is true with `GetHashCode()` and a
   live mutation test (edit through one reference, read the change back
   through the other), and explain the primary-key-keyed lookup that
   causes it.
3. **Part 2 — tracked vs. not tracked, provable through `SaveChanges()`.**
   Edit a tracked entity's property, call `SaveChanges()` with no explicit
   `Update()` call, reread from a fresh context, confirm the edit
   persisted. Do the identical thing to an `AsNoTracking()` entity, call
   `SaveChanges()`, reread, confirm the edit did **not** persist — the
   actual anomaly the task description calls out ("invisible until it
   bites you").
4. **Part 3 — the measured benchmark.** Seed 10,000 rows once. Read all
   10,000 twice per iteration — once tracked, once `AsNoTracking()` — from
   a **fresh `DbContext` each time** (critical: reusing a context would let
   Part 1's identity resolution short-circuit later reads and invalidate
   the comparison). Measure real wall-clock time (`Stopwatch`) and real
   managed-heap allocations (`GC.GetAllocatedBytesForCurrentThread()`,
   forcing a `GC.Collect()` immediately before each measured read so
   leftover garbage from a previous iteration doesn't get counted).
   Discard a warmup iteration (JIT/query-plan-cache warmup would otherwise
   make the very first iteration of *either* path look artificially slow,
   regardless of tracking), then average several measured iterations.
5. **Run it for real and report the actual numbers** — not a calculated
   estimate. See below for where this had to run and the real output.

## Files

```
Day10/
  src/
    EfCoreChangeTracker.Demo.csproj
    Quote.cs
    DemoDbContext.cs
    Program.cs
  docs/
    day10-efcore-change-tracker-submission.md   (this file)
```

`Program.cs` runs all three parts in order and prints everything to the
console: `dotnet run` from the `src/` folder is the entire "how to verify
this" instruction — there's no separate test harness, because the point of
this task is what the console output itself shows.

## A real constraint hit while doing this task, documented honestly

Every other real-database piece of this week's work (Day 7, 8, 9) ran
against a live Azure SQL Database reached through Azure Portal's browser
Query editor, because this sandbox has no direct network route to any SQL
Server. This task is different: it doesn't need a database *server* at
all — the point being demonstrated lives entirely inside EF Core's own
in-process change tracker, and a local SQLite file is enough to give it a
real query pipeline to run against. The actual blocker turned out to be
one level lower: **the cloud sandbox this was written in has no route to
NuGet at all** (`curl -sI https://api.nuget.org/v3/index.json` → `403
Forbidden` via that session's outbound proxy; every nuget.org hostname
tried failed outright), so the code was written and reasoned through there
but could not be executed there. The first real run attempt also caught a
second, smaller issue worth recording: the initial `.csproj` targeted
`net8.0`, but this machine has the .NET 10 SDK installed (matching the
rest of this repo's `QuotesApi` projects) — `dotnet run` failed with "You
must install or update .NET to run this application" until the project
was retargeted to `net10.0` with a matching `Microsoft.EntityFrameworkCore.
Sqlite` version. Both gaps are closed below with a real run on the machine
that actually has this repo's toolchain.

## Real output — run for real on this machine (`dotnet run` from `Day10/src/`)

```
=== Day 10: EF Core change tracker + AsNoTracking() ===
SQLite file: C:\Users\vaish\AppData\Local\Temp\day10-efcore-f672b38874ea40eab8b57297c874be8d.db
Seeded 10,000 rows.

--- Part 1: Identity resolution ---
first  hash: 51801448
second hash: 51801448
ReferenceEquals(first, second) = True
second.Author after mutating first: "MUTATED VIA first"
Why: the change tracker keys tracked entities by PK. Query #2 still hit
SQLite for real, but before materializing a new Quote, EF found Id=42
already tracked and returned that SAME instance instead of a new one.

--- Part 2: Tracked vs. not tracked -- what SaveChanges() actually persists ---
Tracked path -- Id=100 Author after SaveChanges, reread fresh: "EDITED -- TRACKED PATH"
AsNoTracking path -- Id=200 Author after SaveChanges, reread fresh: "Author 200"
(Still the original seeded text -- the edit above was never persisted.)
Why: AsNoTracking() tells EF "don't create a tracked entry for this result".
With no tracked entry, there is nothing for SaveChanges() to compare against,
so an in-memory edit to a no-tracking entity is invisible to the tracker --
not rejected, not warned about, just never turned into SQL.

--- Part 3: Read-path benchmark -- 10,000 rows, tracked vs. AsNoTracking() ---
(1 warmup iteration(s) discarded, then 5 measured iteration(s) averaged)
Tracked (default):
  iteration 1:   46 ms,   10,558,984 bytes allocated
  iteration 2:   46 ms,   10,558,984 bytes allocated
  iteration 3:   64 ms,   10,558,984 bytes allocated
  iteration 4:   44 ms,   10,558,984 bytes allocated
  iteration 5:   67 ms,   10,558,984 bytes allocated
  avg: 53.4 ms, 10,558,984 bytes allocated
AsNoTracking():
  iteration 1:   14 ms,    4,675,904 bytes allocated
  iteration 2:   18 ms,    4,675,904 bytes allocated
  iteration 3:   15 ms,    4,675,904 bytes allocated
  iteration 4:   21 ms,    4,675,904 bytes allocated
  iteration 5:   22 ms,    4,675,904 bytes allocated
  avg: 18.0 ms, 4,675,904 bytes allocated

Time ratio   (tracked / no-tracking): 2.97x
Alloc ratio  (tracked / no-tracking): 2.26x
```

This confirms all three parts for real, not as a modeled or calculated
outcome: identity resolution returns the same object twice (`GetHashCode`
matches, `ReferenceEquals` is `True`, and a mutation through one reference
is visible through the other); the tracked edit to `Id = 100` persisted
through `SaveChanges()` with no explicit `Update()` call, while the
identical edit to the `AsNoTracking()` entity at `Id = 200` silently did
not persist (rereading fresh still shows the original seeded `"Author
200"`); and the 10,000-row read is measurably cheaper without tracking —
**about 3x faster in wall-clock time and a little over 2x fewer bytes
allocated**, on this machine, for this shape of row. The byte figures are
also identical across all 5 iterations within each path (10,558,984 and
4,675,904 respectively) — a sign the measurement itself is stable and not
noisy, not just that the effect exists.

One more real, non-blocking finding from this run, worth recording rather
than hiding: `dotnet run` printed
```
warning NU1903: Package 'SQLitePCLRaw.lib.e_sqlite3' 2.1.11 has a known
high severity vulnerability, https://github.com/advisories/GHSA-2m69-gcr7-jv3q
```
This is a transitive dependency pulled in by `Microsoft.EntityFrameworkCore.
Sqlite` itself, not something this project referenced directly, and it did
not stop the build or affect the demo's correctness — but it's a real
advisory on a real package version this project ended up depending on, and
it's flagged here rather than silently ignored. A production use of this
package would want to check whether a newer `Microsoft.EntityFrameworkCore.
Sqlite` release pulls a patched `SQLitePCLRaw` before shipping.

## What did you learn this session?

That the change tracker's cost isn't a vague "tracking has overhead"
claim — it's a specific, measurable ~3x time and ~2.3x allocation
difference on a 10,000-row read of small rows, which is a genuinely large
gap for something that requires changing one line (`AsNoTracking()`) and
nothing else about the query. What struck me more than the ratio itself
was Part 2's silent failure: the tracked and no-tracking edit code is
identical in shape (`entity.Author = "..."; db.SaveChanges();`), and one of
them just does nothing, with no exception and no warning anywhere in the
output. That's the real "invisible until it bites you" the task description
warns about — a fast read path and a data-loss bug are two sides of the
exact same mechanism, and the only thing separating "I made this endpoint
faster" from "I silently broke this write" is whether a write was ever
supposed to happen on that query's result at all.

## What would break this?

- **A fresh `DbContext` per read is what makes the Part 3 comparison fair.**
  If both the tracked and no-tracking reads happened to reuse the same
  `DbContext` instance across iterations, Part 1's identity resolution
  would kick in on the *tracked* path's later iterations — rows already in
  the tracker's notebook from iteration 1 would be handed back instead of
  freshly materialized, making tracked reads look artificially cheap on
  everything after the first iteration and invalidating the comparison.
  This is exactly why `RunOneRead` opens a new `DemoDbContext` every call.
- **`AsNoTracking()` isn't free of tradeoffs just because it's faster.**
  An endpoint that reads a `Quote`, hands it to the caller, and later
  wants to update it based on the caller's response would need to either
  re-query tracked or explicitly `Attach()`/`Update()` the no-tracking
  instance — `AsNoTracking()` is a correct default for read-only endpoints
  specifically, not a blanket replacement for tracked queries anywhere
  writes might follow.
- **10,000 identical-shape rows in one seed batch is a clean-room number.**
  A table with wide rows, many navigation properties being eagerly
  `Include()`-d, or a mix of already-tracked and freshly-queried entities
  in the same context would change both the absolute numbers and likely
  the ratio between tracked and no-tracking — this benchmark isolates the
  *mechanism*, not a promise that every table would show this exact ratio.
