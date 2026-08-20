# Day 10 — mentor submission (query translation + projections)

## Notes for mentor

Day 10's second task, on its own branch (`day10-query-translation-projections`,
cut from `main` after task 1's PR #28 merged) so it reviews and merges
independently — same convention Day 8's two tasks followed. It lives in its
own folder (`Day10/projections/`) rather than alongside task 1's
`Day10/src/`, because two `.csproj` files in one directory would break
`dotnet run` for both.

```
Day10/
  projections/
    QueryTranslation.Demo.csproj
    Quote.cs
    QuoteListDto.cs
    DemoDbContext.cs
    SqlCapture.cs
    TextHelpers.cs
    Program.cs
  docs/
    day10-query-translation-projections-submission.md   (this file)
```

`dotnet run` from `Day10/projections/` is the whole verification story — the
program prints every generated SQL statement and every measurement itself.

## What this task actually asks for, in simple words

When you write LINQ against a `DbContext`, EF Core does not run your C#
against the database. It _translates_ your query into SQL, sends that SQL,
and builds objects from whatever comes back. The trap is that the
translation is invisible: two LINQ queries that look almost identical can
produce wildly different SQL, and the only way to know which one you wrote
is to look at the SQL EF actually sent.

Three specific things follow from that, and this task asks for all three:

1. **See the SQL.** `LogTo(...)` on `DbContextOptionsBuilder` prints every
   statement EF executes. `EnableSensitiveDataLogging()` additionally prints
   the real parameter _values_ instead of redacting them — which is what
   makes a logged statement reproducible by hand, and also why it is a
   development-only switch (those values are real user data).

2. **Ask for fewer columns.** `db.Quotes.ToList()` generates a `SELECT` naming
   every mapped column — including a 600-character `Text` column a list
   endpoint never displays. Rewriting it as
   `.Select(q => new QuoteListDto { Id = q.Id, Author = q.Author })` makes EF
   build its `SELECT` list from the _projection_ instead of from the entity,
   so the columns the DTO doesn't mention are columns the database is never
   asked for and never sends.

3. **Catch accidental client-side evaluation.** Sometimes part of your query
   silently doesn't make it into the SQL, and runs in C# over rows already
   pulled into memory instead. The result is still _correct_, which is
   exactly why it survives code review — it's just doing far more work than
   the code appears to. Reading the logged SQL is how you catch it.

## Implementation plan (written before writing the code)

1. **A small, self-contained EF Core project** — its own `Quote` model
   (deliberately the same shape as `QuotesApi.Models.Quote`, plus a wide
   `Text` column so column count actually costs something) and its own
   `DbContext`, on SQLite. Not a dependency on `QuotesApi`: query
   translation is EF Core core behavior and this shouldn't drag in
   QuotesApi's auth, migrations, and SQL Server requirement just to print
   SQL. SQLite specifically, **not** EF Core's `InMemory` provider —
   `InMemory` generates no SQL at all, so a task about which SQL EF emits
   would have literally nothing to show.
2. **Part 1 — log the SQL.** Run the same query twice, once with `LogTo`
   alone and once with `LogTo` + `EnableSensitiveDataLogging()`, and print
   the _full_ log message both times (not just the statement) so the
   difference — redacted vs. real parameter value — is visible where it
   actually appears, in the `Parameters=[...]` preamble.
3. **Part 2 — the rewrite.** Load 10,000 whole entities, then the same
   10,000 rows through a `QuoteListDto` projection. Print both generated
   `SELECT` statements side by side, and measure time and allocations for
   each. `AsNoTracking()` on both sides, so this isolates the projection
   rather than re-measuring the change-tracker overhead task 1 already
   covered.
4. **Part 3 — catch the accident.** Three separate cases, because they fail
   in genuinely different ways and only one of them is protected by EF:
   - **3a** `ToList()` before `Where()` — the filter never becomes a `WHERE`
     clause. Silent; correct answer; 10,000 rows fetched to return 40.
   - **3b** an untranslatable C# method inside the projection — the DTO
     looks narrow, but EF fetches the full `Text` column to run the method
     in memory, quietly undoing Part 2's entire win. Also silent.
   - **3c** an untranslatable _predicate_ (`StringComparison` overload) —
     EF Core 3.0+ throws instead of falling back to client evaluation.
     Included for contrast: it shows the safety net that exists, and by
     omission, that 3a and 3b fall outside it.
5. **Capture the SQL programmatically** (`SqlCapture`) rather than making the
   reader scroll through interleaved console output matching statements to
   queries by eye. Same `LogTo` mechanism the exercise asks for, pointed at
   a list instead of the console.
6. **Warm up before measuring.** The first query on a given
   `DbContextOptions` pays for EF's model build and query-pipeline setup.
   Without a warmup, whichever variant ran first would absorb that one-time
   cost and look slower for a reason unrelated to its SQL.

## Bugs caught in my own code while writing this, worth recording

Two were caught by reasoning through the code before running it; two more
only showed up in the first real run. All four are recorded because three of
them would have silently weakened the evidence rather than failing loudly:

- **A local function cannot appear in an expression tree.** Part 3b needs an
  untranslatable C# method _inside_ a projection. Written as a local
  function (the natural choice in a top-level-statements `Program.cs`), it
  isn't a client-side-evaluation demo at all — it's compile error CS8110,
  because the compiler refuses to put a local function reference into an
  expression tree in the first place. It has to be a `static` method on a
  class (`TextHelpers.Truncate`) for the compiler to emit the call into the
  tree and for EF to then discover it can't translate it. Getting this wrong
  would have looked like "the demo doesn't build" rather than "the demo is
  demonstrating the wrong thing."
- **My own SQL-capture helper would have hidden Part 1's whole point.** The
  helper strips the log preamble to keep the side-by-side SQL readable — but
  `EnableSensitiveDataLogging()` changes the preamble (`Parameters=[...]`),
  not the SQL text. Filtering to statement text only would have printed two
  _identical_ strings and "proved" the flag does nothing. Part 1 therefore
  prints the full, unmodified log message; only Parts 2 and 3 use the
  stripped view.
- **Part 3c's exception was being truncated before the useful part** (found in
  run 1, fixed for run 2). EF embeds a pretty-printed expression tree in that
  message, so it spans many lines — and printing only the first line cut it
  off at `The LINQ expression 'DbSet<Quote>()`, i.e. _before_ the words "could
  not be translated" that say what actually went wrong. Now flattened and
  word-wrapped so the whole message survives, which is how the real cause
  (`Translation of the 'string.Equals' overload with a 'StringComparison'
parameter is not supported`) became visible at all.
- **Continuation lines were double-indented, and my first fix for it caused a
  second regression.** EF indents a statement's `FROM`/`WHERE` six spaces
  relative to its `SELECT`; adding a display prefix on top pushed them further
  right than the `SELECT`, making flat queries look nested. The obvious fix —
  strip the shared indent inside the display helper — worked for Parts 2/3 but
  _flattened Part 1_, because the same six spaces there legitimately nest a log
  record's continuation lines under its `info:` header. Run 2 showed exactly
  that. The correct fix is to dedent **at extraction** (`SqlCapture.Statements()`,
  which is the only view whose first line has been sliced mid-line and so is
  the only one missing its own indent), leaving the full-log view untouched.

  Because these four helpers are plain C# with no EF dependency, they were then
  pulled into a standalone harness and tested directly rather than re-checked
  by eye: 11 assertions covering both text shapes, including a guard that
  reproduces the Part 1 regression before confirming the fix, and a nested-
  subquery case proving the dedent strips only the _common_ indent. All 11
  pass, and the third real run confirmed it end to end. (Verifying this in the
  NuGet-less sandbox needed a `nuget.config` with `<clear />` — with no package
  sources configured, restore of a dependency-free project has nothing to fetch
  and succeeds offline.)

  The wider point, and the reason this is written up rather than quietly
  patched: a formatting bug in a _diagnostic_ is not a cosmetic bug. Both of
  these were bugs in the code whose entire job is to show what EF is doing —
  one hid the cause of an exception, the other misrepresented the shape of a
  SQL statement. On a task whose whole lesson is "don't trust what the query
  looks like, read what it actually sent", getting the reading apparatus wrong
  is the one failure that undermines every other conclusion.

## Real output

Real captured output — `dotnet run` from `Day10/projections/`, .NET 10 SDK,
EF Core 10.0.0, SQLite. Verbatim, and from the final run: the one where both
display fixes described above are in place, so Part 1's log nesting and Part
3c's full message are both correct here.

```
=== Day 10 (task 2): Query translation + projections ===
SQLite file: C:\Users\vaish\AppData\Local\Temp\day10-projections-203058014ad3467683dc42e001993bda.db
Seeded 10,000 rows (250 distinct authors, ~615 chars of Text per row).

--- Part 1: Logging the generated SQL ---
(a) .LogTo(...) only -- full log message as EF emitted it:
  info: 08/20/2026 10:50:31.857 RelationalEventId.CommandExecuted[20101] (Microsoft.EntityFrameworkCore.Database.Command)
        Executed DbCommand (0ms) [Parameters=[@targetAuthor='?' (Size = 8)], CommandType='Text', CommandTimeout='30']
        SELECT "q"."Id"
        FROM "Quotes" AS "q"
        WHERE "q"."Author" = @targetAuthor

(b) .LogTo(...) + .EnableSensitiveDataLogging() -- same query:
  info: 08/20/2026 10:50:31.916 RelationalEventId.CommandExecuted[20101] (Microsoft.EntityFrameworkCore.Database.Command)
        Executed DbCommand (0ms) [Parameters=[@targetAuthor='Author 7' (Size = 8)], CommandType='Text', CommandTimeout='30']
        SELECT "q"."Id"
        FROM "Quotes" AS "q"
        WHERE "q"."Author" = @targetAuthor

--- Part 2: Whole entities vs. .Select(x => new QuoteListDto { ... }) ---
BEFORE -- pulls whole entities:
  C#:   var rows = db.Quotes.AsNoTracking().ToList();
  SQL:
        SELECT "q"."Id", "q"."Author", "q"."CreatedAt", "q"."CreatedByUserId", "q"."Text"
        FROM "Quotes" AS "q"
  ==>   10,000 rows, 50 ms, 17,081,128 bytes allocated

AFTER -- projects to a DTO:
  C#:   var rows = db.Quotes.AsNoTracking()
            .Select(q => new QuoteListDto { Id = q.Id, Author = q.Author })
            .ToList();
  SQL:
        SELECT "q"."Id", "q"."Author"
        FROM "Quotes" AS "q"
  ==>   10,000 rows, 13 ms, 3,092,304 bytes allocated

  Time:        50 ms -> 13 ms   (3.85x less)
  Allocations: 17,081,128 -> 3,092,304 bytes   (5.52x less)

--- Part 3a: Accidental client-side evaluation -- ToList() called too early ---
ACCIDENT -- .ToList() before .Where():
  C#:   db.Quotes.AsNoTracking().ToList()        // <-- materializes EVERYTHING
          .Where(q => q.Author == targetAuthor)  // <-- now in-memory LINQ
          .Select(...).ToList();
  SQL:
        SELECT "q"."Id", "q"."Author", "q"."CreatedAt", "q"."CreatedByUserId", "q"."Text"
        FROM "Quotes" AS "q"
  ==>   returned 40 rows, 29 ms, 16,800,312 bytes allocated

FIXED -- .Where() while it is still an IQueryable:
  C#:   db.Quotes.AsNoTracking()
          .Where(q => q.Author == targetAuthor)
          .Select(...).ToList();
  SQL:
        SELECT "q"."Id", "q"."Author"
        FROM "Quotes" AS "q"
        WHERE "q"."Author" = @targetAuthor
  ==>   returned 40 rows, 2 ms, 156,608 bytes allocated

  Time:        29 ms -> 2 ms   (14.50x less)
  Allocations: 16,800,312 -> 156,608 bytes   (107.28x less)

--- Part 3b: Accidental client-side evaluation -- a C# method in the projection ---
ACCIDENT -- Preview = TextHelpers.Truncate(q.Text, 30):
  SQL:
        SELECT "q"."Id", "q"."Text"
        FROM "Quotes" AS "q"
  ==>   10,000 rows, 31 ms, 16,271,008 bytes allocated

FIXED -- Preview = q.Text.Substring(0, 30):
  SQL:
        SELECT "q"."Id", substr("q"."Text", 0 + 1, 30) AS "Preview"
        FROM "Quotes" AS "q"
  ==>   10,000 rows, 18 ms, 3,525,464 bytes allocated

  Time:        31 ms -> 18 ms   (1.72x less)
  Allocations: 16,271,008 -> 3,525,464 bytes   (4.62x less)

--- Part 3c: The case EF Core refuses to evaluate client-side ---
EF Core threw InvalidOperationException, as intended:
  The LINQ expression 'DbSet<Quote>() .Where(q => q.Author.Equals( value:
  @targetAuthor, comparisonType: OrdinalIgnoreCase))' could not be translated.
  Additional information: Translation of the 'string.Equals' overload with a
  'StringComparison' parameter is not supported. See
  https://go.microsoft.com/fwlink/?linkid=2129535 for more information. Either
  rewrite the query in a form that can be translated, or switch to client
  evaluation explicitly by inserting a call to 'AsEnumerable',
  'AsAsyncEnumerable', 'ToList', or 'ToListAsync'. See
  https://go.microsoft.com/fwlink/?linkid=2101038 for more information.
```

### Run-to-run stability

Worth stating explicitly, because it changes how much weight each number
deserves. Across three runs of the above:

| Measure             | Run 1   | Run 2   | Run 3   |
| ------------------- | ------- | ------- | ------- |
| Part 2 allocations  | 5.52x   | 5.52x   | 5.52x   |
| Part 3a allocations | 107.13x | 107.17x | 107.28x |
| Part 3b allocations | 4.62x   | 4.62x   | 4.62x   |
| Part 2 time         | 3.77x   | 4.13x   | 3.85x   |
| Part 3a time        | 9.00x   | 8.25x   | 14.50x  |
| Part 3b time        | 1.78x   | 2.90x   | 1.72x   |

The allocation ratios are effectively constant; the time ratios swing by a
factor of nearly two (Part 3a: 8.25x to 14.50x). That is the expected shape —
allocations are deterministic for a fixed row count and column set, wall-clock
time on a developer machine is not — and it is why the conclusions below are
stated in terms of allocations, with timings treated as directional only.
Quoting "14.50x faster" as _the_ result of this exercise would be
cherry-picking the luckiest of three runs.

### What the output actually establishes

**Part 1** — the SQL text is byte-identical between (a) and (b); the only
difference is `@targetAuthor='?'` versus `@targetAuthor='Author 7'` in the
`Parameters=[...]` preamble. That is a useful thing to have seen directly,
because it pins down exactly what the sensitive-data switch does and does not
affect: it is not "more SQL detail", it is _user data in your logs_.

**Part 2** — the rewrite worked exactly as intended. `SELECT "q"."Id",
"q"."Author", "q"."CreatedAt", "q"."CreatedByUserId", "q"."Text"` became
`SELECT "q"."Id", "q"."Author"`, for the same 10,000 rows: **5.52x fewer bytes
allocated** (identical across all three runs), with time consistently around
4x. Note the allocation ratio beats the time ratio, which makes sense — the
column that disappeared is the wide one, and its cost is mostly _memory_
(10,000 strings of ~615 characters) rather than query execution.

**Part 3a** — the accidental client-side evaluation, caught exactly the way
the exercise intends: the accident's SQL has **no `WHERE` clause at all**,
while the fixed version's does. Both returned the same 40 correct rows. The
gap is the largest in this whole submission: **107x allocations** (16.8 MB
versus 157 KB, stable across all three runs) and somewhere between 8x and 14x
time depending on the run, because the fixed query fetches 40 narrow rows
where the accident fetched 10,000 wide ones to throw 9,960 of them away in C#.

**Part 3b** — the subtler accident, and the one I'd have been most likely to
ship. Both versions project into a DTO holding an `Id` and a 30-character
`Preview`, so both look equally narrow in C#. But `TextHelpers.Truncate(q.Text, 30)`
produced `SELECT "q"."Id", "q"."Text"` — the entire wide column, fetched in
full and truncated in memory — while `q.Text.Substring(0, 30)` produced
`SELECT "q"."Id", substr("q"."Text", 0 + 1, 30) AS "Preview"`, doing the
truncation in the database. **4.62x fewer allocations** for what is, in C#,
a cosmetically identical projection. (The `0 + 1` in the generated SQL is
EF translating .NET's 0-based `Substring` index into SQLite's 1-based
`substr` — a small, concrete reminder that the SQL is genuinely _translated_,
not merely passed through.)

**Part 3c** — EF Core threw `InvalidOperationException` rather than silently
filtering in memory, confirming the EF Core 3.0+ behavior. The full message
names the precise cause (`Translation of the 'string.Equals' overload with a
'StringComparison' parameter is not supported`) and, notably, spells out the
escape hatch: _"switch to client evaluation explicitly by inserting a call to
'AsEnumerable', 'AsAsyncEnumerable', 'ToList', or 'ToListAsync'."_ That is
worth reading next to Part 3a — EF is pointing at the very construct that,
used unintentionally, produces 3a's silent 107x accident. The difference
between the sanctioned escape hatch and the bug is nothing but intent, which
is precisely why the generated SQL has to be checked rather than assumed.

## What did you learn this session?

That "look at the generated SQL" is not a debugging tip you reach for when
something is slow — it is the only way to know what your query does at all.
Part 3b is the case that made this land: two projections that are cosmetically
identical in C# (same DTO, same two properties, same 30-character preview)
produced completely different SQL, and the expensive one is the one a
reviewer would wave through, because it _looks_ like the optimisation. Nothing
in the C# distinguishes them; only `SELECT "q"."Id", "q"."Text"` versus
`SELECT "q"."Id", substr(...)` does. The related realisation is how narrow
EF's safety net actually is: EF Core 3.0's decision to throw on
untranslatable predicates is genuinely valuable, but it covers _only_
predicates — the two accidents that cost the most here (premature `ToList()`,
client evaluation in a final projection) are both perfectly legal, both
silent, and both return the correct answer. Correct-but-expensive is the
failure mode to actually watch for, and the log is the only place it is
visible.

## What would break this?

- **Projection only helps if the projection is honest.** Part 3b is the
  cautionary case: a DTO with two small properties still dragged the full
  600-character column across the wire, because _how_ one of those
  properties was computed forced EF to fetch the source column. "I projected
  to a DTO" is not by itself evidence that the SQL got narrower — the
  logged `SELECT` list is the only evidence.
- **`Select` into a DTO gives up the change tracker on purpose.** A DTO isn't
  an entity, so nothing projected this way can be edited and saved back;
  it's the right shape for read endpoints and the wrong shape for anything
  that then wants to write. That's the same tradeoff task 1 found with
  `AsNoTracking()`, arrived at from a different direction.
- **EF Core 3.0's "throw instead of client-evaluate" only covers predicates.**
  It is easy to over-generalise that change into "EF will tell me if my
  query isn't translating." It won't: premature `ToList()` (3a) is legal C#
  EF has no way to object to, and client evaluation in a final projection
  (3b) is deliberately still allowed. Both are silent, and both are the
  common real-world versions of this mistake.
- **These numbers come from SQLite on a local disk, with no network between
  the app and the database.** That understates the projection win rather
  than overstating it: against a real remote database (QuotesApi's Azure SQL
  Database), the columns you don't fetch also aren't serialised, sent over a
  network, and re-parsed. The _ratio_ here is a floor, not a ceiling — but
  it's also not a number to quote as if it were measured against Azure.
