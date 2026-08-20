using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using EfCoreChangeTracker.Demo;

// =============================================================================
// Day 10 -- EF Core change tracker + AsNoTracking()
// =============================================================================
// Three real, executed demonstrations, in the order the exercise asks for:
//   1. Identity resolution -- the same row, queried twice inside one
//      DbContext, comes back as the SAME .NET object (ReferenceEquals),
//      not two separate objects with equal data.
//   2. Tracked vs. not tracked -- editing a tracked entity's property and
//      calling SaveChanges() persists it with no explicit Update() call;
//      editing an AsNoTracking() entity the same way and calling
//      SaveChanges() persists NOTHING, because the tracker never saw the
//      edit happen.
//   3. The read-path win -- load 10,000 rows twice, once tracked (the
//      default) and once with AsNoTracking(), and print real elapsed time
//      and real managed-heap allocations for each, from a fresh DbContext
//      each time so the tracker starts empty.
//
// Uses a real on-disk SQLite database (via Microsoft.EntityFrameworkCore.
// Sqlite) rather than the EF Core InMemory provider on purpose: InMemory
// doesn't implement a real query pipeline (no SQL is generated or
// executed), so identity resolution and AsNoTracking() would appear to
// work but for the wrong reasons, and the timing/allocation comparison in
// part 3 would be measuring InMemory's own bookkeeping, not the actual
// materialization cost AsNoTracking() is meant to reduce. SQLite is a
// real relational engine with a real EF Core provider, so every behavior
// demonstrated here is the same one the real Azure SQL Database backing
// QuotesApi would show under Microsoft.EntityFrameworkCore.SqlServer --
// the change tracker itself is provider-agnostic; nothing below depends on
// which database engine is underneath it.

var dbPath = Path.Combine(Path.GetTempPath(), $"day10-efcore-{Guid.NewGuid():N}.db");
var connectionString = $"Data Source={dbPath}";

Console.WriteLine("=== Day 10: EF Core change tracker + AsNoTracking() ===");
Console.WriteLine($"SQLite file: {dbPath}");
Console.WriteLine();

try
{
    await SeedAsync(connectionString, rowCount: 10_000);

    RunIdentityResolutionDemo(connectionString);
    Console.WriteLine();

    RunTrackedVsNoTrackingSaveDemo(connectionString);
    Console.WriteLine();

    RunReadPathBenchmark(connectionString, rowCount: 10_000, warmupIterations: 1, measuredIterations: 5);
}
finally
{
    SqliteConnection.ClearAllPools(); // release the file handle before deleting
    if (File.Exists(dbPath))
        File.Delete(dbPath);
}

// =============================================================================
static async Task SeedAsync(string connectionString, int rowCount)
{
    var options = new DbContextOptionsBuilder<DemoDbContext>()
        .UseSqlite(connectionString)
        .Options;

    using var db = new DemoDbContext(options);
    await db.Database.EnsureCreatedAsync();

    var quotes = Enumerable.Range(1, rowCount).Select(i => new Quote
    {
        Author = $"Author {i % 250}",       // 250 distinct authors across 10k rows
        Text = $"Quote text number {i} -- {new string('x', 40)}"
    });

    db.Quotes.AddRange(quotes);
    await db.SaveChangesAsync();

    Console.WriteLine($"Seeded {rowCount:N0} rows.");
    Console.WriteLine();
}

// =============================================================================
// Part 1 -- identity resolution: two queries for the SAME row, inside the
// SAME DbContext / same logical unit of work, return the identical .NET
// object -- not two separate objects that happen to have equal data.
static void RunIdentityResolutionDemo(string connectionString)
{
    Console.WriteLine("--- Part 1: Identity resolution ---");

    var options = new DbContextOptionsBuilder<DemoDbContext>()
        .UseSqlite(connectionString)
        .Options;

    using var db = new DemoDbContext(options);

    var first = db.Quotes.First(q => q.Id == 42);
    var second = db.Quotes.First(q => q.Id == 42);

    Console.WriteLine($"first  hash: {first.GetHashCode()}");
    Console.WriteLine($"second hash: {second.GetHashCode()}");
    Console.WriteLine($"ReferenceEquals(first, second) = {ReferenceEquals(first, second)}");

    // Prove it's not a coincidence of value-equality: mutate through
    // "first" and read the same change back through "second" -- there is
    // only one object in memory, so both variables see the same mutation.
    first.Author = "MUTATED VIA first";
    Console.WriteLine($"second.Author after mutating first: \"{second.Author}\"");

    // What actually causes this: EF Core's change tracker keys tracked
    // entities by their primary key. The SECOND query still sends real SQL
    // to SQLite (this isn't a query-level cache) -- but before materializing
    // a new object from the row it got back, EF checks "do I already have
    // an entity tracked with Id = 42?" and if so, discards the freshly
    // read column values and hands back the EXISTING tracked instance
    // instead. That's identity resolution: one tracked entity per key, per
    // DbContext, no matter how many times you query for it.
    Console.WriteLine();
    Console.WriteLine("Why: the change tracker keys tracked entities by PK. Query #2 still hit");
    Console.WriteLine("SQLite for real, but before materializing a new Quote, EF found Id=42");
    Console.WriteLine("already tracked and returned that SAME instance instead of a new one.");
}

// =============================================================================
// Part 2 -- tracked vs. not tracked, and what SaveChanges() actually does
// with each. This is the anomaly that "bites you": editing an AsNoTracking()
// entity compiles fine, runs fine, and silently does nothing on SaveChanges.
static void RunTrackedVsNoTrackingSaveDemo(string connectionString)
{
    Console.WriteLine("--- Part 2: Tracked vs. not tracked -- what SaveChanges() actually persists ---");

    var options = new DbContextOptionsBuilder<DemoDbContext>()
        .UseSqlite(connectionString)
        .Options;

    // --- Tracked edit: no explicit Update() call needed ---
    using (var db = new DemoDbContext(options))
    {
        var tracked = db.Quotes.First(q => q.Id == 100); // tracked by default
        tracked.Author = "EDITED -- TRACKED PATH";
        // No db.Quotes.Update(tracked) call. The change tracker already
        // knows this instance's Author property changed (it snapshots/
        // detects changes at SaveChanges time via the tracked entry), so
        // this alone is enough for an UPDATE statement to be generated.
        db.SaveChanges();
    }

    using (var db = new DemoDbContext(options))
    {
        var reread = db.Quotes.First(q => q.Id == 100);
        Console.WriteLine($"Tracked path -- Id=100 Author after SaveChanges, reread fresh: \"{reread.Author}\"");
    }

    // --- AsNoTracking() edit: the same code, one method call different ---
    using (var db = new DemoDbContext(options))
    {
        var untracked = db.Quotes.AsNoTracking().First(q => q.Id == 200);
        untracked.Author = "EDITED -- NO-TRACKING PATH (should NOT persist)";
        // The change tracker has no entry for this object at all -- it was
        // never told to watch it. SaveChanges() asks the tracker "what
        // changed?", the tracker has nothing for this entity, so it
        // generates zero SQL for this edit. This is not an error or an
        // exception -- it silently does nothing, which is exactly why
        // this is the anomaly that "bites you": the code looks identical
        // to the tracked version above.
        db.SaveChanges();
    }

    using (var db = new DemoDbContext(options))
    {
        var reread = db.Quotes.First(q => q.Id == 200);
        Console.WriteLine($"AsNoTracking path -- Id=200 Author after SaveChanges, reread fresh: \"{reread.Author}\"");
        Console.WriteLine("(Still the original seeded text -- the edit above was never persisted.)");
    }

    Console.WriteLine();
    Console.WriteLine("Why: AsNoTracking() tells EF \"don't create a tracked entry for this result\".");
    Console.WriteLine("With no tracked entry, there is nothing for SaveChanges() to compare against,");
    Console.WriteLine("so an in-memory edit to a no-tracking entity is invisible to the tracker --");
    Console.WriteLine("not rejected, not warned about, just never turned into SQL.");
}

// =============================================================================
// Part 3 -- the actual read-path measurement the exercise asks for: real
// elapsed time and real managed-heap allocations for a 10,000-row read,
// tracked vs. AsNoTracking(), each from a FRESH DbContext so the tracker
// starts empty every time (a warm tracker from a previous query would
// contaminate the comparison via identity resolution from Part 1).
static void RunReadPathBenchmark(string connectionString, int rowCount, int warmupIterations, int measuredIterations)
{
    Console.WriteLine($"--- Part 3: Read-path benchmark -- {rowCount:N0} rows, tracked vs. AsNoTracking() ---");
    Console.WriteLine($"({warmupIterations} warmup iteration(s) discarded, then {measuredIterations} measured iteration(s) averaged)");
    Console.WriteLine();

    var options = new DbContextOptionsBuilder<DemoDbContext>()
        .UseSqlite(connectionString)
        .Options;

    for (var i = 0; i < warmupIterations; i++)
    {
        RunOneRead(options, tracked: true);
        RunOneRead(options, tracked: false);
    }

    var trackedResults = new List<(long Ms, long Bytes)>();
    var noTrackingResults = new List<(long Ms, long Bytes)>();

    for (var i = 0; i < measuredIterations; i++)
    {
        trackedResults.Add(RunOneRead(options, tracked: true));
        noTrackingResults.Add(RunOneRead(options, tracked: false));
    }

    PrintSummary("Tracked (default)", trackedResults);
    PrintSummary("AsNoTracking()", noTrackingResults);

    var avgTrackedMs = trackedResults.Average(r => r.Ms);
    var avgNoTrackingMs = noTrackingResults.Average(r => r.Ms);
    var avgTrackedBytes = trackedResults.Average(r => r.Bytes);
    var avgNoTrackingBytes = noTrackingResults.Average(r => r.Bytes);

    Console.WriteLine();
    Console.WriteLine($"Time ratio   (tracked / no-tracking): {avgTrackedMs / Math.Max(avgNoTrackingMs, 1):N2}x");
    Console.WriteLine($"Alloc ratio  (tracked / no-tracking): {avgTrackedBytes / Math.Max(avgNoTrackingBytes, 1):N2}x");
}

static (long Ms, long Bytes) RunOneRead(DbContextOptions<DemoDbContext> options, bool tracked)
{
    using var db = new DemoDbContext(options); // fresh context => empty tracker every time

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var sw = Stopwatch.StartNew();

    var query = tracked
        ? db.Quotes.AsQueryable()
        : db.Quotes.AsNoTracking();

    var rows = query.ToList(); // materialize -- this is the cost being measured

    sw.Stop();
    var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

    var count = rows.Count; // touch the result so it isn't optimized away
    var trackedCount = tracked ? db.ChangeTracker.Entries().Count() : 0;

    if (tracked && trackedCount != count)
        throw new InvalidOperationException($"Expected {count} tracked entries, found {trackedCount}.");

    return (sw.ElapsedMilliseconds, allocatedAfter - allocatedBefore);
}

static void PrintSummary(string label, List<(long Ms, long Bytes)> results)
{
    Console.WriteLine($"{label}:");
    for (var i = 0; i < results.Count; i++)
        Console.WriteLine($"  iteration {i + 1}: {results[i].Ms,4} ms, {results[i].Bytes,12:N0} bytes allocated");

    Console.WriteLine($"  avg: {results.Average(r => r.Ms):N1} ms, {results.Average(r => r.Bytes):N0} bytes allocated");
}
