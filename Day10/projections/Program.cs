using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using QueryTranslation.Demo;

// =============================================================================
// Day 10 (task 2) -- Query translation + projections
// =============================================================================
// Four real, executed demonstrations, in the order the exercise asks for:
//
//   Part 1  Log the generated SQL -- LogTo(...) + EnableSensitiveDataLogging(),
//           and what the second one actually changes in the output (plus why it
//           is a development-only switch).
//   Part 2  Take a query that pulls whole entities and rewrite it as
//           .Select(x => new QuoteListDto { ... }) -- then show that the SQL
//           SELECT list really did shrink, and measure what that saved.
//   Part 3  Catch accidental client-side evaluation. Three cases, because they
//           fail in genuinely different ways:
//             3a  ToList() called too early -- the WHERE never reaches SQL.
//                 Silent. Right answer, wrong amount of work.
//             3b  A local static method inside the projection -- the projection
//                 looks narrow but the wide column is still fetched. Silent,
//                 and it quietly undoes Part 2's entire win.
//             3c  An untranslatable predicate -- EF Core throws instead of
//                 silently evaluating in memory. The safety net that DOES
//                 exist, shown for contrast with 3a and 3b, which it misses.
//
// Runs against a real on-disk SQLite database rather than EF Core's InMemory
// provider on purpose: InMemory generates no SQL at all, so a demo about which
// SQL EF produces would have nothing to show. Query translation and projection
// live in EF Core's provider-independent core, so what is demonstrated here is
// the same behavior QuotesApi's real QuotesDbContext shows against Azure SQL
// Database -- only the SQL dialect in the printed statements would differ.

var dbPath = Path.Combine(Path.GetTempPath(), $"day10-projections-{Guid.NewGuid():N}.db");
var connectionString = $"Data Source={dbPath}";

const int RowCount = 10_000;
const string TargetAuthor = "Author 7";

Console.WriteLine("=== Day 10 (task 2): Query translation + projections ===");
Console.WriteLine($"SQLite file: {dbPath}");
Console.WriteLine();

try
{
    await SeedAsync(connectionString, RowCount);

    RunPart1_LogGeneratedSql(connectionString, TargetAuthor);
    Console.WriteLine();

    RunPart2_WholeEntityVsProjection(connectionString, RowCount);
    Console.WriteLine();

    RunPart3a_PrematureToList(connectionString, TargetAuthor, RowCount);
    Console.WriteLine();

    RunPart3b_ClientSideEvalInProjection(connectionString);
    Console.WriteLine();

    RunPart3c_UntranslatablePredicate(connectionString, TargetAuthor);
}
finally
{
    SqliteConnection.ClearAllPools();
    if (File.Exists(dbPath))
        File.Delete(dbPath);
}

// =============================================================================
static async Task SeedAsync(string connectionString, int rowCount)
{
    var options = new DbContextOptionsBuilder<DemoDbContext>()
        .UseSqlite(connectionString)
        .Options;

    await using var db = new DemoDbContext(options);
    await db.Database.EnsureCreatedAsync();

    var seededAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    var quotes = Enumerable.Range(1, rowCount).Select(i => new Quote
    {
        Author = $"Author {i % 250}",                          // 250 authors, 40 rows each
        Text = $"Quote number {i}. " + new string('x', 600),   // deliberately wide
        CreatedByUserId = i % 3 == 0 ? null : $"user-{i % 50}",
        CreatedAt = seededAt.AddMinutes(i)
    });

    db.Quotes.AddRange(quotes);
    await db.SaveChangesAsync();

    Console.WriteLine($"Seeded {rowCount:N0} rows (250 distinct authors, ~615 chars of Text per row).");
}

// =============================================================================
// PART 1 -- log the generated SQL, and show what EnableSensitiveDataLogging
// actually changes about that log.
static void RunPart1_LogGeneratedSql(string connectionString, string targetAuthor)
{
    Console.WriteLine("--- Part 1: Logging the generated SQL ---");
    Console.WriteLine();

    // (a) LogTo only -- no EnableSensitiveDataLogging.
    var redacted = new SqlCapture();
    var redactedOptions = new DbContextOptionsBuilder<DemoDbContext>()
        .UseSqlite(connectionString)
        .LogTo(redacted.Add, new[] { DbLoggerCategory.Database.Command.Name }, LogLevel.Information)
        .Options;

    Warmup(redactedOptions);
    redacted.Clear();

    using (var db = new DemoDbContext(redactedOptions))
    {
        _ = db.Quotes.Where(q => q.Author == targetAuthor).Select(q => q.Id).ToList();
    }

    Console.WriteLine("(a) .LogTo(...) only -- full log message as EF emitted it:");
    Console.WriteLine(Indent(redacted.LastFullMessage()));
    Console.WriteLine();

    // (b) LogTo + EnableSensitiveDataLogging.
    var sensitive = new SqlCapture();
    var sensitiveOptions = sensitive.Options(connectionString);

    Warmup(sensitiveOptions);
    sensitive.Clear();

    using (var db = new DemoDbContext(sensitiveOptions))
    {
        _ = db.Quotes.Where(q => q.Author == targetAuthor).Select(q => q.Id).ToList();
    }

    Console.WriteLine("(b) .LogTo(...) + .EnableSensitiveDataLogging() -- same query:");
    Console.WriteLine(Indent(sensitive.LastFullMessage()));
    Console.WriteLine();
    Console.WriteLine("The SQL text is identical in both. The difference is in the Parameters=[...]");
    Console.WriteLine($"preamble: (a) redacts the value, (b) shows the real one (\"{targetAuthor}\").");
    Console.WriteLine("That is what makes a logged statement copy-pasteable into a SQL client to");
    Console.WriteLine("reproduce by hand -- and exactly why it belongs in development configuration");
    Console.WriteLine("only: in a real app those parameter values are real user data being written");
    Console.WriteLine("into the log sink.");
}

// =============================================================================
// PART 2 -- the rewrite the exercise asks for: whole entities -> DTO projection,
// with proof the SELECT list shrank.
static void RunPart2_WholeEntityVsProjection(string connectionString, int rowCount)
{
    Console.WriteLine("--- Part 2: Whole entities vs. .Select(x => new QuoteListDto { ... }) ---");
    Console.WriteLine();

    // --- BEFORE: pull whole entities ---
    var beforeCapture = new SqlCapture();
    var beforeOptions = beforeCapture.Options(connectionString);
    Warmup(beforeOptions);

    // AsNoTracking on both sides so this isolates the projection, not the
    // change-tracker overhead Day 10's first task already measured separately.
    var before = Measure(beforeCapture, () =>
    {
        using var db = new DemoDbContext(beforeOptions);
        return db.Quotes.AsNoTracking().ToList().Count;
    });

    Console.WriteLine("BEFORE -- pulls whole entities:");
    Console.WriteLine("  C#:   var rows = db.Quotes.AsNoTracking().ToList();");
    Console.WriteLine("  SQL:");
    Console.WriteLine(Indent(beforeCapture.SingleStatement(), "        "));
    Console.WriteLine($"  ==>   {before.Count:N0} rows, {before.Ms:N0} ms, {before.Bytes:N0} bytes allocated");
    Console.WriteLine();

    // --- AFTER: project to only the columns the caller needs ---
    var afterCapture = new SqlCapture();
    var afterOptions = afterCapture.Options(connectionString);
    Warmup(afterOptions);

    var after = Measure(afterCapture, () =>
    {
        using var db = new DemoDbContext(afterOptions);
        return db.Quotes
            .AsNoTracking()
            .Select(q => new QuoteListDto { Id = q.Id, Author = q.Author })
            .ToList()
            .Count;
    });

    Console.WriteLine("AFTER -- projects to a DTO:");
    Console.WriteLine("  C#:   var rows = db.Quotes.AsNoTracking()");
    Console.WriteLine("            .Select(q => new QuoteListDto { Id = q.Id, Author = q.Author })");
    Console.WriteLine("            .ToList();");
    Console.WriteLine("  SQL:");
    Console.WriteLine(Indent(afterCapture.SingleStatement(), "        "));
    Console.WriteLine($"  ==>   {after.Count:N0} rows, {after.Ms:N0} ms, {after.Bytes:N0} bytes allocated");
    Console.WriteLine();

    Console.WriteLine($"Same {rowCount:N0} rows either way. The difference is the SELECT list: BEFORE names");
    Console.WriteLine("every mapped column (including the ~615-character Text, plus CreatedByUserId and");
    Console.WriteLine("CreatedAt, none of which the caller asked for); AFTER names only Id and Author,");
    Console.WriteLine("because EF Core builds its SELECT list from the projection, not the entity type.");
    Console.WriteLine();
    Console.WriteLine($"  Time:        {before.Ms:N0} ms -> {after.Ms:N0} ms   ({Ratio(before.Ms, after.Ms)} less)");
    Console.WriteLine($"  Allocations: {before.Bytes:N0} -> {after.Bytes:N0} bytes   ({Ratio(before.Bytes, after.Bytes)} less)");
}

// =============================================================================
// PART 3a -- accidental client-side evaluation, the silent kind: ToList() before
// Where(), so the filter never becomes a WHERE clause.
static void RunPart3a_PrematureToList(string connectionString, string targetAuthor, int rowCount)
{
    Console.WriteLine("--- Part 3a: Accidental client-side evaluation -- ToList() called too early ---");
    Console.WriteLine();

    var badCapture = new SqlCapture();
    var badOptions = badCapture.Options(connectionString);
    Warmup(badOptions);

    var bad = Measure(badCapture, () =>
    {
        using var db = new DemoDbContext(badOptions);
        // ToList() here ENDS the translatable query. Everything after it is
        // LINQ-to-Objects running over 10,000 already-materialized entities.
        return db.Quotes.AsNoTracking().ToList()
            .Where(q => q.Author == targetAuthor)
            .Select(q => new QuoteListDto { Id = q.Id, Author = q.Author })
            .ToList()
            .Count;
    });

    Console.WriteLine("ACCIDENT -- .ToList() before .Where():");
    Console.WriteLine("  C#:   db.Quotes.AsNoTracking().ToList()        // <-- materializes EVERYTHING");
    Console.WriteLine("          .Where(q => q.Author == targetAuthor)  // <-- now in-memory LINQ");
    Console.WriteLine("          .Select(...).ToList();");
    Console.WriteLine("  SQL:");
    Console.WriteLine(Indent(badCapture.SingleStatement(), "        "));
    Console.WriteLine($"  ==>   returned {bad.Count:N0} rows, {bad.Ms:N0} ms, {bad.Bytes:N0} bytes allocated");
    Console.WriteLine();

    var goodCapture = new SqlCapture();
    var goodOptions = goodCapture.Options(connectionString);
    Warmup(goodOptions);

    var good = Measure(goodCapture, () =>
    {
        using var db = new DemoDbContext(goodOptions);
        return db.Quotes.AsNoTracking()
            .Where(q => q.Author == targetAuthor)
            .Select(q => new QuoteListDto { Id = q.Id, Author = q.Author })
            .ToList()
            .Count;
    });

    Console.WriteLine("FIXED -- .Where() while it is still an IQueryable:");
    Console.WriteLine("  C#:   db.Quotes.AsNoTracking()");
    Console.WriteLine("          .Where(q => q.Author == targetAuthor)");
    Console.WriteLine("          .Select(...).ToList();");
    Console.WriteLine("  SQL:");
    Console.WriteLine(Indent(goodCapture.SingleStatement(), "        "));
    Console.WriteLine($"  ==>   returned {good.Count:N0} rows, {good.Ms:N0} ms, {good.Bytes:N0} bytes allocated");
    Console.WriteLine();

    Console.WriteLine($"This is the accident worth internalising: BOTH return the same {good.Count} correct");
    Console.WriteLine("rows, and nothing warns you. The only visible difference is in the logged SQL --");
    Console.WriteLine($"the accident's statement has NO WHERE clause, so the database sent all {rowCount:N0}");
    Console.WriteLine($"rows (with their full Text column) across the wire for C# to discard {rowCount - good.Count:N0}");
    Console.WriteLine("of them in memory.");
    Console.WriteLine($"  Time:        {bad.Ms:N0} ms -> {good.Ms:N0} ms   ({Ratio(bad.Ms, good.Ms)} less)");
    Console.WriteLine($"  Allocations: {bad.Bytes:N0} -> {good.Bytes:N0} bytes   ({Ratio(bad.Bytes, good.Bytes)} less)");
}

// =============================================================================
// PART 3b -- accidental client-side evaluation, the sneaky kind: an
// untranslatable static method inside the projection. The DTO is narrow; the
// SQL is not.
static void RunPart3b_ClientSideEvalInProjection(string connectionString)
{
    Console.WriteLine("--- Part 3b: Accidental client-side evaluation -- a C# method in the projection ---");
    Console.WriteLine();

    var badCapture = new SqlCapture();
    var badOptions = badCapture.Options(connectionString);
    Warmup(badOptions);

    var bad = Measure(badCapture, () =>
    {
        using var db = new DemoDbContext(badOptions);
        // TextHelpers.Truncate is ordinary C#. EF Core cannot translate it, and
        // in a FINAL projection it does not complain -- it silently fetches
        // whatever the method needs (Text, in full) and runs it in memory.
        return db.Quotes.AsNoTracking()
            .Select(q => new QuotePreviewDto { Id = q.Id, Preview = TextHelpers.Truncate(q.Text, 30) })
            .ToList()
            .Count;
    });

    Console.WriteLine("ACCIDENT -- Preview = TextHelpers.Truncate(q.Text, 30):");
    Console.WriteLine("  SQL:");
    Console.WriteLine(Indent(badCapture.SingleStatement(), "        "));
    Console.WriteLine($"  ==>   {bad.Count:N0} rows, {bad.Ms:N0} ms, {bad.Bytes:N0} bytes allocated");
    Console.WriteLine();

    var goodCapture = new SqlCapture();
    var goodOptions = goodCapture.Options(connectionString);
    Warmup(goodOptions);

    var good = Measure(goodCapture, () =>
    {
        using var db = new DemoDbContext(goodOptions);
        // Substring IS translatable -- the truncation happens in the database and
        // only 30 characters per row ever cross the wire.
        return db.Quotes.AsNoTracking()
            .Select(q => new QuotePreviewDto { Id = q.Id, Preview = q.Text.Substring(0, 30) })
            .ToList()
            .Count;
    });

    Console.WriteLine("FIXED -- Preview = q.Text.Substring(0, 30):");
    Console.WriteLine("  SQL:");
    Console.WriteLine(Indent(goodCapture.SingleStatement(), "        "));
    Console.WriteLine($"  ==>   {good.Count:N0} rows, {good.Ms:N0} ms, {good.Bytes:N0} bytes allocated");
    Console.WriteLine();

    Console.WriteLine("Both produce identical DTOs, and both projections mention only Id and a");
    Console.WriteLine("30-character Preview -- so both LOOK equally narrow in C#. The logged SQL is");
    Console.WriteLine("where they part company: the accident selects the whole Text column and");
    Console.WriteLine("truncates it in memory, quietly undoing the entire point of projecting.");
    Console.WriteLine($"  Time:        {bad.Ms:N0} ms -> {good.Ms:N0} ms   ({Ratio(bad.Ms, good.Ms)} less)");
    Console.WriteLine($"  Allocations: {bad.Bytes:N0} -> {good.Bytes:N0} bytes   ({Ratio(bad.Bytes, good.Bytes)} less)");
}

// =============================================================================
// PART 3c -- for contrast: the case EF Core DOES protect you from.
static void RunPart3c_UntranslatablePredicate(string connectionString, string targetAuthor)
{
    Console.WriteLine("--- Part 3c: The case EF Core refuses to evaluate client-side ---");
    Console.WriteLine();

    var capture = new SqlCapture();
    var options = capture.Options(connectionString);

    try
    {
        using var db = new DemoDbContext(options);
        // StringComparison overloads have no SQL equivalent. Before EF Core 3.0
        // this silently became a client-side filter (the same accident as 3a, but
        // invisible in the source). Since 3.0 it is a hard error instead.
        var rows = db.Quotes.AsNoTracking()
            .Where(q => q.Author.Equals(targetAuthor, StringComparison.OrdinalIgnoreCase))
            .Select(q => new QuoteListDto { Id = q.Id, Author = q.Author })
            .ToList();

        Console.WriteLine($"Unexpectedly succeeded, returning {rows.Count:N0} rows -- this provider");
        Console.WriteLine("translated it after all. SQL:");
        Console.WriteLine(Indent(capture.SingleStatement(), "  "));
    }
    catch (InvalidOperationException ex)
    {
        Console.WriteLine("EF Core threw InvalidOperationException, as intended:");
        // EF embeds a pretty-printed expression tree in this message, so it
        // arrives spread over many lines. Printing only the first line (an
        // earlier version of this file's mistake) cuts it off before the words
        // "could not be translated" -- i.e. before the part that actually says
        // what went wrong. Flatten and wrap instead, so the whole message
        // survives and still fits the console.
        Console.WriteLine(Indent(Wrap(Flatten(ex.Message), 76), "  "));
    }

    Console.WriteLine();
    Console.WriteLine("Why this one is safe and 3a/3b are not: EF Core 3.0 stopped silently falling");
    Console.WriteLine("back to client-side evaluation for a PREDICATE it cannot translate, so an");
    Console.WriteLine("untranslatable WHERE now fails loudly on first run instead of quietly");
    Console.WriteLine("downloading the table forever. What it did NOT do is cover the two cases above:");
    Console.WriteLine("calling ToList() early is legal C# that EF has no way to object to, and client");
    Console.WriteLine("evaluation in a FINAL projection is still deliberately allowed. Reading the");
    Console.WriteLine("logged SQL remains the only way to catch those two.");
}

// =============================================================================
// Helpers
//
// Warmup matters for honest numbers: the first query on a given
// DbContextOptions pays for EF's model build and query-pipeline setup. Without
// this, whichever variant happened to run first would absorb that one-time cost
// and look slower for a reason that has nothing to do with the SQL it emits.
static void Warmup(DbContextOptions<DemoDbContext> options)
{
    using var db = new DemoDbContext(options);
    _ = db.Quotes.AsNoTracking().Select(q => q.Id).Take(1).ToList();
}

static (int Count, long Ms, long Bytes) Measure(SqlCapture capture, Func<int> action)
{
    capture.Clear();

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var before = GC.GetAllocatedBytesForCurrentThread();
    var sw = Stopwatch.StartNew();

    var count = action();

    sw.Stop();
    var after = GC.GetAllocatedBytesForCurrentThread();

    return (count, sw.ElapsedMilliseconds, after - before);
}

static string Ratio(long before, long after) =>
    after <= 0 ? "n/a" : $"{(double)before / after:N2}x";

/// <summary>
/// Prefixes every line, and nothing more. Any dedenting a sliced SQL statement
/// needs happens in SqlCapture.Statements(), at extraction -- see the comment
/// there for why doing it here instead breaks Part 1.
/// </summary>
static string Indent(string text, string prefix = "  ") =>
    string.Join(Environment.NewLine,
        text.Split('\n').Select(line => prefix + line.TrimEnd('\r', ' ')));

/// <summary>Collapses all whitespace runs (including newlines) to single spaces.</summary>
static string Flatten(string text) =>
    string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

/// <summary>Greedy word wrap, so a long single-line message stays readable.</summary>
static string Wrap(string text, int width)
{
    var lines = new List<string>();
    var current = new System.Text.StringBuilder();

    foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        if (current.Length > 0 && current.Length + 1 + word.Length > width)
        {
            lines.Add(current.ToString());
            current.Clear();
        }

        if (current.Length > 0)
            current.Append(' ');

        current.Append(word);
    }

    if (current.Length > 0)
        lines.Add(current.ToString());

    return string.Join('\n', lines);
}
