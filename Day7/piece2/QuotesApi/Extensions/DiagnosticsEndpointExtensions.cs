using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Extensions;

/// <summary>
/// Day 11 -- a deliberately slow endpoint, plus the tooling needed to profile
/// it and the fixed version to compare against.
///
/// WHY THESE ENDPOINTS ARE NOT BEHIND .RequireAuthorization()
/// Every real endpoint in this API requires a token (see
/// QuoteEndpointExtensions). These deliberately do not, and that is a
/// measurement decision rather than laziness: this endpoint exists to have
/// its p50/p99 measured under sustained load. Putting auth in front of it
/// would mean every load-test request either carries a token the harness has
/// to mint and refresh, or spends time in token validation -- and either way
/// the latency distribution being measured would include work that has
/// nothing to do with the N+1 this exercise is about. A profile is only
/// useful if the thing being profiled is the thing under test.
///
/// WHY THAT IS SAFE HERE
/// MapDiagnosticsEndpoints refuses to map anything unless the app is running
/// in Development OR "Diagnostics:Enabled" is explicitly true in
/// configuration. In any deployed environment (where ASPNETCORE_ENVIRONMENT
/// is Production and the flag is absent) these routes do not exist at all --
/// not 401, not 403, simply not registered. That is a stronger guarantee than
/// an auth check, because there is no credential anywhere that can reach
/// them.
///
/// These endpoints also mutate data (seeding rows, creating and dropping an
/// index). That is the other reason they must never exist in production, and
/// the reason the seed endpoint is a POST rather than a GET.
/// </summary>
public static class DiagnosticsEndpointExtensions
{
    private const string AuthorIndexName = "IX_Quotes_Author";

    public static WebApplication MapDiagnosticsEndpoints(this WebApplication app)
    {
        var explicitlyEnabled = app.Configuration.GetValue<bool>("Diagnostics:Enabled");

        if (!app.Environment.IsDevelopment() && !explicitlyEnabled)
            return app;

        var group = app.MapGroup("/api/diagnostics");

        // ------------------------------------------------------------------
        // THE SLOW ENDPOINT -- the subject of this exercise.
        // ------------------------------------------------------------------
        // Two compounding problems, on purpose:
        //
        //   1. N+1 queries. One query to list the distinct authors, then one
        //      more query PER AUTHOR to count that author's quotes. With 500
        //      authors that is 501 round trips to the database to answer one
        //      HTTP request. Every one of them is a separate command, a
        //      separate parse, and a separate result set.
        //
        //   2. A missing index. dbo.Quotes has never had an index on Author
        //      (see QuotesDbContext.OnModelCreating -- Quote is the one
        //      entity with no explicit configuration at all), so each of
        //      those 500 per-author queries has no choice but to scan the
        //      whole table. The two problems multiply rather than add: 500
        //      scans of a 50,000-row table is 25,000,000 rows examined to
        //      return 500 numbers.
        //
        // The response deliberately carries its own timing and query count.
        // Reporting them from inside the request is what lets a load-test
        // result be cross-checked against what the endpoint itself believes
        // happened, instead of trusting the harness alone.
        group.MapGet("/authors-quotes-nplus1", async (
            QuotesDbContext db,
            CancellationToken cancellationToken) =>
        {
            var stopwatch = Stopwatch.StartNew();

            // Query #1: the distinct author list.
            var authors = await db.Quotes
                .AsNoTracking()
                .Select(q => q.Author)
                .Distinct()
                .ToListAsync(cancellationToken);

            var results = new List<AuthorQuoteCount>(authors.Count);

            // Queries #2..#N+1: one per author. THIS is the N+1.
            foreach (var author in authors)
            {
                var count = await db.Quotes
                    .AsNoTracking()
                    .Where(q => q.Author == author)
                    .CountAsync(cancellationToken);

                results.Add(new AuthorQuoteCount(author, count));
            }

            stopwatch.Stop();

            return Results.Ok(new
            {
                strategy = "n+1 (one query per author, no index on Author)",
                queriesIssued = authors.Count + 1,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                authorCount = results.Count,
                authors = results.OrderByDescending(r => r.QuoteCount).Take(10)
            });
        });

        // ------------------------------------------------------------------
        // THE FIXED ENDPOINT -- same answer, one query.
        // ------------------------------------------------------------------
        // Identical response shape to the endpoint above, so the two are
        // directly comparable under the same load test. The only difference
        // is that the grouping happens in the database instead of in a C#
        // loop, which collapses 501 round trips into 1.
        //
        // Note this is a fix for the N+1 specifically, NOT for the missing
        // index -- a single GROUP BY still has to read the whole table once.
        // Keeping the two fixes separate is deliberate: it lets the profile
        // show how much of the cost was the round trips and how much was the
        // scanning, rather than fixing both at once and being unable to
        // attribute the improvement.
        group.MapGet("/authors-quotes-grouped", async (
            QuotesDbContext db,
            CancellationToken cancellationToken) =>
        {
            var stopwatch = Stopwatch.StartNew();

            var results = await db.Quotes
                .AsNoTracking()
                .GroupBy(q => q.Author)
                .Select(g => new AuthorQuoteCount(g.Key, g.Count()))
                .ToListAsync(cancellationToken);

            stopwatch.Stop();

            return Results.Ok(new
            {
                strategy = "single GROUP BY",
                queriesIssued = 1,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                authorCount = results.Count,
                authors = results.OrderByDescending(r => r.QuoteCount).Take(10)
            });
        });

        // ------------------------------------------------------------------
        // Profiling support: seed enough rows for the problem to be visible.
        // ------------------------------------------------------------------
        // 20 rows cannot demonstrate an N+1 -- 21 fast queries against a tiny
        // table is still fast. The shape of the seed matters as much as the
        // size: authorCount controls how many per-author queries the N+1
        // endpoint will issue, and count controls how much each of those
        // queries has to scan. Both are parameters so the profile can be
        // re-run at different shapes.
        //
        // Uses EF AddRange in batches rather than a provider-specific bulk
        // INSERT: this runs once before a profiling session, so being a few
        // seconds slower than raw SQL costs nothing, and staying
        // provider-agnostic means the same endpoint works against SQLite
        // locally and SQL Server if pointed at one.
        group.MapPost("/seed", async (
            int count,
            int authorCount,
            QuotesDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (count < 1 || count > 200_000)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["count"] = new[] { "count must be between 1 and 200000." }
                });

            if (authorCount < 1 || authorCount > count)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["authorCount"] = new[] { "authorCount must be between 1 and count." }
                });

            var stopwatch = Stopwatch.StartNew();
            const int batchSize = 5_000;
            var padding = new string('x', 200);

            for (var offset = 0; offset < count; offset += batchSize)
            {
                var batch = Enumerable
                    .Range(offset, Math.Min(batchSize, count - offset))
                    // Quote.Create, not an object initializer. Quote's own doc
                    // comment says the factory exists so its validation rules
                    // cannot be bypassed "no matter who's constructing a
                    // Quote", and names "a background import job" as the case
                    // it is guarding against -- which is precisely what this
                    // seeding endpoint is. This synthetic data would pass the
                    // rules anyway (author well under 200 chars, text ~220
                    // under 1000), so going through the factory costs nothing
                    // and keeps the one place that decides what a valid Quote
                    // is actually authoritative.
                    .Select(i => Quote.Create(
                        author: $"Perf Author {i % authorCount}",
                        text: $"Perf seed quote {i}. {padding}"))
                    .ToList();

                db.Quotes.AddRange(batch);
                await db.SaveChangesAsync(cancellationToken);

                // Each batch is its own unit of work; clearing the tracker
                // between batches keeps a 50,000-row seed from holding every
                // inserted entity in memory for the whole operation (exactly
                // the change-tracker cost Day 10's first task measured).
                db.ChangeTracker.Clear();
            }

            stopwatch.Stop();

            return Results.Ok(new
            {
                inserted = count,
                distinctAuthorsAdded = authorCount,
                totalRowsNow = await db.Quotes.CountAsync(cancellationToken),
                elapsedMs = stopwatch.ElapsedMilliseconds
            });
        });

        // ------------------------------------------------------------------
        // Profiling support: add or remove the missing index at runtime.
        // ------------------------------------------------------------------
        // Deliberately NOT an EF migration. A migration would make the index
        // a permanent part of the schema, which is the right thing for a real
        // fix but the wrong thing for this exercise -- the whole point is to
        // measure the same endpoint under the same load with the index
        // present and absent, in one sitting. Being able to toggle it makes
        // that a controlled experiment instead of two runs of two different
        // builds.
        //
        // The real fix, once measured, belongs in QuotesDbContext as
        // entity.HasIndex(x => x.Author) plus a generated migration. This
        // endpoint is the measuring instrument, not the fix.
        group.MapPost("/author-index", async (
            bool enabled,
            QuotesDbContext db,
            CancellationToken cancellationToken) =>
        {
            var provider = db.Database.ProviderName ?? "unknown";
            var isSqlServer = provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase);

            // CREATE INDEX is portable; DROP INDEX is not -- SQL Server wants
            // "DROP INDEX name ON table", SQLite wants just "DROP INDEX name".
            var sql = enabled
                ? $"CREATE INDEX {AuthorIndexName} ON Quotes (Author)"
                : isSqlServer
                    ? $"DROP INDEX {AuthorIndexName} ON Quotes"
                    : $"DROP INDEX {AuthorIndexName}";

            try
            {
                await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
                return Results.Ok(new { provider, indexPresent = enabled, executed = sql });
            }
            catch (Exception ex)
            {
                // Creating an index that exists, or dropping one that does
                // not, is the expected failure here and is not interesting --
                // report it plainly rather than letting it surface as a 500,
                // so a profiling script can call this idempotently.
                return Results.Ok(new
                {
                    provider,
                    indexPresent = (bool?)null,
                    executed = sql,
                    note = "Statement failed -- most likely the index already exists (enabled=true) or does not exist (enabled=false).",
                    error = ex.Message
                });
            }
        });

        // ------------------------------------------------------------------
        // Profiling support: what state is the database actually in?
        // ------------------------------------------------------------------
        // Worth having because every number in a profile is meaningless
        // without it. A p99 of 4 seconds means nothing unless it is known
        // that it was measured over 50,000 rows and 500 authors with no
        // index -- and that is exactly the sort of context that gets lost
        // between running a load test and writing up its result.
        group.MapGet("/stats", async (
            QuotesDbContext db,
            CancellationToken cancellationToken) =>
        {
            var totalRows = await db.Quotes.CountAsync(cancellationToken);
            var distinctAuthors = await db.Quotes
                .Select(q => q.Author)
                .Distinct()
                .CountAsync(cancellationToken);

            return Results.Ok(new
            {
                provider = db.Database.ProviderName,
                totalQuoteRows = totalRows,
                distinctAuthors,
                queriesTheNPlus1EndpointWillIssue = distinctAuthors + 1
            });
        });

        return app;
    }

    private record AuthorQuoteCount(string Author, int QuoteCount);
}
