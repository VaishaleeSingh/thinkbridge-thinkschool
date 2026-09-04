using System.Diagnostics;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Resilience;

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
        // Day 12 task 2 -- the SAME query, hand-written and run through Dapper.
        // ------------------------------------------------------------------
        // Two decisions here exist to keep the comparison honest, and both are
        // easy to get wrong in a way that flatters Dapper:
        //
        //   1. It borrows EF's connection via db.Database.GetDbConnection()
        //      rather than opening its own. Dapper on a fresh SqliteConnection
        //      would be measuring connection setup as well as mapping, and
        //      would also sidestep EF's pooling -- so the "win" would partly be
        //      an artefact of the harness. Sharing the connection means the only
        //      thing that differs between this endpoint and the one above is
        //      how a result set becomes objects.
        //
        //   2. The SQL is deliberately written to match what EF generates for
        //      the grouped query, not to be cleverer than it. EF emits
        //      SELECT "q"."Author", COUNT(*) FROM "Quotes" AS "q" GROUP BY "q"."Author"
        //      and there is nothing to improve on -- so any measured difference
        //      is the cost of EF's query pipeline and materializer, which is the
        //      thing actually under test. Hand-writing a *better* query would be
        //      a different (and much less interesting) experiment: it would
        //      prove that better SQL is faster, not that Dapper is.
        //
        // The response shape is identical to the EF endpoint's so bombardier
        // compares like with like.
        group.MapGet("/authors-quotes-dapper", async (
            QuotesDbContext db,
            CancellationToken cancellationToken) =>
        {
            var stopwatch = Stopwatch.StartNew();

            // No aliasing gymnastics needed: the column names match the record's
            // parameter names, so Dapper's default mapping does the work. That
            // is Dapper's actual value proposition -- it is a mapper, not a
            // query builder, and it expects you to own the SQL.
            const string sql = """
                SELECT Author, COUNT(*) AS QuoteCount
                FROM Quotes
                GROUP BY Author
                """;

            var connection = db.Database.GetDbConnection();

            // AsList(), not ToList(). QueryAsync<T> already buffers into a
            // List<T> internally and hands it back as IEnumerable<T>, so
            // ToList() copies all 500 rows a SECOND time on every request --
            // while EF's ToListAsync() returns its list with no copy. The first
            // version of this endpoint had exactly that flaw, and it made the
            // Dapper side look ~20% slower under load than it actually is.
            // Dapper ships AsList() precisely for this: it returns the existing
            // List when there is one and only copies when there is not.
            var results = (await connection.QueryAsync<AuthorQuoteRow>(
                new CommandDefinition(sql, cancellationToken: cancellationToken)))
                .AsList();

            stopwatch.Stop();

            return Results.Ok(new
            {
                strategy = "single GROUP BY via Dapper",
                queriesIssued = 1,
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
        // Day 12 task 2, part 2 -- the case where Dapper SHOULD win.
        // ------------------------------------------------------------------
        // The grouped-query comparison above came out against Dapper, and the
        // reason was structural: 500 narrow rows give a mapper almost nothing
        // to do, so the query cost dominated and both were the same. That is a
        // real result, but it only measures one end of the range. Asserting
        // "Dapper earns its place on large result sets" off the back of it
        // would be reasoning past the evidence.
        //
        // So this pair moves the one variable that should matter: the amount of
        // materialization. Same table, same provider, same load -- but now
        // thousands of WIDE rows (Text is ~620 characters) instead of 500
        // narrow ones. If the mapper is ever the bottleneck, it is here.
        //
        // Both endpoints deliberately materialize into the SAME DTO and then
        // return only a summary -- a count and the total text length. That is
        // what isolates mapping cost:
        //   - serialising thousands of wide rows to JSON would dominate the
        //     measurement and is identical work for both, so it is excluded;
        //   - projecting to the same type on both sides means neither gets an
        //     advantage from a different object shape;
        //   - summing the text lengths forces every row to be fully
        //     materialized rather than lazily skipped.

        group.MapGet("/quotes-wide-ef", async (
            int? rows,
            QuotesDbContext db,
            CancellationToken cancellationToken) =>
        {
            var take = rows is > 0 and <= 50_000 ? rows.Value : 5_000;
            var stopwatch = Stopwatch.StartNew();

            var results = await db.Quotes
                .AsNoTracking()
                .OrderBy(q => q.Id)
                .Take(take)
                .Select(q => new QuoteWideRow
                {
                    Id = q.Id,
                    Author = q.Author,
                    Text = q.Text,
                    CreatedByUserId = q.CreatedByUserId,
                    BackgroundImageUrl = q.BackgroundImageUrl
                })
                .ToListAsync(cancellationToken);

            stopwatch.Stop();

            return Results.Ok(new
            {
                strategy = "wide rows via EF projection",
                rowsRequested = take,
                rowsMaterialized = results.Count,
                totalTextLength = results.Sum(r => (long)r.Text.Length),
                elapsedMs = stopwatch.ElapsedMilliseconds
            });
        });

        group.MapGet("/quotes-wide-dapper", async (
            int? rows,
            QuotesDbContext db,
            CancellationToken cancellationToken) =>
        {
            var take = rows is > 0 and <= 50_000 ? rows.Value : 5_000;
            var stopwatch = Stopwatch.StartNew();

            // Same shape, same ordering, same row count as the EF endpoint --
            // and again written to match what EF emits rather than to beat it,
            // so the comparison stays about mapping and not about SQL.
            const string sql = """
                SELECT Id, Author, Text, CreatedByUserId, BackgroundImageUrl
                FROM Quotes
                ORDER BY Id
                LIMIT @take
                """;

            var connection = db.Database.GetDbConnection();

            var results = (await connection.QueryAsync<QuoteWideRow>(
                new CommandDefinition(sql, new { take }, cancellationToken: cancellationToken)))
                .AsList();

            stopwatch.Stop();

            return Results.Ok(new
            {
                strategy = "wide rows via Dapper",
                rowsRequested = take,
                rowsMaterialized = results.Count,
                totalTextLength = results.Sum(r => (long)r.Text.Length),
                elapsedMs = stopwatch.ElapsedMilliseconds
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

        // ------------------------------------------------------------------
        // Day 19 -- Dead-letter peek  (Development-gated)
        // ------------------------------------------------------------------
        // A health-check for the "audit" subscription's DLQ. Returns the
        // first N dead-lettered messages' metadata — id, reason, description,
        // enqueuedAt — WITHOUT the body, which may contain user content.
        //
        // The real operational answer is an Azure Monitor alert on
        // DeadletteredMessages > 0 for the subscription. This endpoint is the
        // Development-time equivalent: a quick "what is in the DLQ?" without
        // opening the portal. It should not exist in production and does not,
        // because MapDiagnosticsEndpoints returns early unless IsDevelopment()
        // or Diagnostics:Enabled is true.
        //
        // Replay path (described, not built): receive from the DLQ, fix or
        // re-publish to the topic, complete the dead-lettered copy. Replay
        // re-enters the idempotent handler — which is exactly why the
        // ProcessedMessages retention window must exceed the DLQ dwell time.
        group.MapGet("/quote-events/dead-letters", async (
            int? maxMessages,
            IConfiguration config,
            IServiceProvider services,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var sbEnabled = config.GetValue<bool>("ServiceBus:Enabled");
            if (!sbEnabled)
                return Results.Ok(new { note = "ServiceBus is disabled (ServiceBus:Enabled=false). No DLQ to peek." });

            var ns = config["ServiceBus:FullyQualifiedNamespace"];
            var topic = config["ServiceBus:TopicName"] ?? "quote-events";
            var sub = config["ServiceBus:AuditSubscription"] ?? "audit";

            if (string.IsNullOrWhiteSpace(ns))
                return Results.Problem("ServiceBus:FullyQualifiedNamespace is not configured.");

            var max = Math.Min(maxMessages ?? 10, 50); // Safety cap

            try
            {
                // Reuse the singleton client registered in MessagingExtensions
                // rather than opening a second AMQP connection per request --
                // one connection per call is the classic Service Bus
                // performance bug, and a diagnostics route is no exception.
                var client = services.GetService<Azure.Messaging.ServiceBus.ServiceBusClient>();
                if (client is null)
                    return Results.Problem("Service Bus client is not registered.");

                var entityPath = $"{topic}/Subscriptions/{sub}";
                await using var receiver = client.CreateReceiver(topic, sub,
                    new Azure.Messaging.ServiceBus.ServiceBusReceiverOptions
                    {
                        SubQueue = Azure.Messaging.ServiceBus.SubQueue.DeadLetter,
                        ReceiveMode = Azure.Messaging.ServiceBus.ServiceBusReceiveMode.PeekLock
                    });

                var messages = await receiver.PeekMessagesAsync(max, cancellationToken: cancellationToken);

                var result = messages.Select(m => new
                {
                    MessageId = m.MessageId,
                    EnqueuedAt = m.EnqueuedTime,
                    DeadLetterReason = m.DeadLetterReason,
                    DeadLetterErrorDescription = m.DeadLetterErrorDescription,
                    DeliveryCount = m.DeliveryCount,
                    EventType = m.ApplicationProperties.TryGetValue("eventType", out var et) ? et : null,
                    SchemaVersion = m.ApplicationProperties.TryGetValue("schemaVersion", out var sv) ? sv : null,
                    // Body deliberately omitted: may contain user content.
                });

                return Results.Ok(new
                {
                    subscription = $"{entityPath}/$DeadLetterQueue",
                    count = messages.Count,
                    messages = result
                });
            }
            catch (Exception ex)
            {
                // Log the detail, return none of it. The repository's rule
                // since Day 18 is that exception text never crosses an HTTP
                // boundary -- a broker exception can carry entity paths,
                // namespace names and token-acquisition detail.
                loggerFactory
                    .CreateLogger("QuotesApi.Diagnostics.DeadLetters")
                    .LogError(ex, "Failed to peek the dead-letter queue for {Topic}/{Subscription}", topic, sub);

                return Results.Problem(
                    title: "Could not read the dead-letter queue.",
                    detail: "See the application logs for the failure detail.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // ------------------------------------------------------------------
        // Day 22 -- Resilience state  (Development-gated)
        // ------------------------------------------------------------------
        // What the circuit breaker is doing right now, plus the counters the
        // Day 22 proof is read from.
        //
        // WHY THIS IS THE INSTRUMENT AND NOT THE LOGS. Day 5's breaker logged
        // three messages and was otherwise invisible: nothing could ask it
        // "are you open?". A live demonstration then had to infer its state
        // from response latency, and an inference is not evidence -- a slow
        // response, a shed request and an open circuit are indistinguishable
        // from the outside. CircuitBreakerStateProvider reports the state
        // directly, so the run recorded in Day22/verification is a reading
        // rather than an interpretation.
        //
        // Read-only, and no connection strings or token material: state, and
        // counts.
        group.MapGet("/resilience", (
            CircuitBreakerRegistry circuitBreaker,
            ResilienceMetrics metrics,
            IOptions<ResilienceOptions> options) =>
        {
            var o = options.Value;

            return Results.Ok(new
            {
                circuitState = circuitBreaker.StateName,
                circuitStateValue = circuitBreaker.StateAsGaugeValue,

                transitions = new
                {
                    opened = metrics.CircuitOpened,
                    halfOpened = metrics.CircuitHalfOpened,
                    closed = metrics.CircuitClosed
                },

                retries = metrics.Retries,

                // Read this one next to retries, always. A zero here with a
                // non-zero retries count means every failure that was retried
                // was idempotent; a non-zero here is the gate refusing to
                // repeat a write, which is the only evidence the gate exists.
                retriesSuppressed = metrics.RetriesSuppressed,

                bulkheadRejections = metrics.BulkheadRejections,

                policy = new
                {
                    totalTimeout = o.TotalTimeout,
                    attemptTimeout = o.AttemptTimeout,
                    retryAttempts = o.Retry.MaxAttempts,
                    retryBaseDelay = o.Retry.BaseDelay,
                    idempotentOnly = o.Retry.IdempotentOnly,
                    failureRatio = o.CircuitBreaker.FailureRatio,
                    minimumThroughput = o.CircuitBreaker.MinimumThroughput,
                    samplingDuration = o.CircuitBreaker.SamplingDuration,
                    breakDuration = o.CircuitBreaker.BreakDuration,
                    bulkheadPermits = o.Bulkhead.PermitLimit,
                    bulkheadQueue = o.Bulkhead.QueueLimit
                }
            });
        });

        // Trips the circuit by hand, for a live walkthrough that should not
        // require making login.microsoftonline.com fail.
        //
        // THIS IS NOT THE PROOF, and it is worth being blunt about why:
        // isolating a breaker through its manual control demonstrates that the
        // manual control works. It says nothing about whether SUSTAINED
        // FAILURE opens the circuit, which is the actual Day 22 claim. That
        // claim is proven by CircuitBreakerLifecycleTests driving real
        // failures through the real strategy and asserting on the state
        // provider. This route exists for demonstration convenience only.
        //
        // POST, because it mutates state -- and Development-gated with
        // everything else in this group.
        group.MapPost("/resilience/isolate", async (
            CircuitBreakerRegistry circuitBreaker,
            CancellationToken cancellationToken) =>
        {
            await circuitBreaker.ManualControl.IsolateAsync(cancellationToken);

            return Results.Ok(new
            {
                circuitState = circuitBreaker.StateName,
                note = "Isolated by hand. This demonstrates the manual control, NOT that "
                     + "sustained failure opens the circuit -- see CircuitBreakerLifecycleTests "
                     + "for that. Isolated is sticky: it stays isolated until /resilience/close."
            });
        });

        group.MapPost("/resilience/close", async (
            CircuitBreakerRegistry circuitBreaker,
            CancellationToken cancellationToken) =>
        {
            await circuitBreaker.ManualControl.CloseAsync(cancellationToken);

            return Results.Ok(new { circuitState = circuitBreaker.StateName });
        });

        // ------------------------------------------------------------------
        // Day 22 -- Resilience probe  (Development-gated)
        // ------------------------------------------------------------------
        // Issues one outbound GET through the SAME named client and the SAME
        // Polly pipeline the Entra ID backchannel uses, against a URL the
        // caller chooses, and reports what the pipeline did with it.
        //
        // WHY THIS EXISTS, because a probe endpoint can easily be a test
        // fixture leaking into production code and this one has to justify
        // itself:
        //
        // The obvious way to make the live run fail is to point
        // AzureAd:Authority at a dead address. Two things defeat that, and
        // both were found by trying it rather than by reasoning about it:
        //
        //   1. JwtBearer refuses to INITIALIZE with an http:// authority
        //      ("The MetadataAddress or Authority must use HTTPS unless
        //      disabled for development by setting RequireHttpsMetadata =
        //      false"), and throws from PostConfigure -- before any network
        //      call. Every request then fails in 3ms having touched nothing,
        //      which looks like a broken pipeline and is not one.
        //
        //   2. Fixed by using https://, the deeper problem appears:
        //      ConfigurationManager CACHES a failed metadata retrieval for its
        //      refresh interval. A burst of N requests therefore produces one
        //      HTTP attempt, not N, and the breaker never sees enough failures
        //      to open. The experiment would be measuring
        //      ConfigurationManager's caching, not the circuit breaker.
        //
        // Both are properties of the CALLER, not of the pipeline. So the probe
        // removes the caller: it drives the pipeline directly, which is the
        // thing Day 22 is making a claim about. The breaker instance is shared
        // -- one named client, one handler chain, one CircuitBreakerRegistry --
        // so a circuit opened through this probe IS the circuit that protects
        // token validation.
        //
        // Development-gated with everything else in this group, GET and
        // side-effect-free apart from the pipeline state it deliberately
        // exercises.
        group.MapGet("/resilience/probe", async (
            string? url,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            // Default: a port nothing listens on, so the failure is a
            // connection refusal -- fast, unambiguous, and entirely local. No
            // load is generated against anyone else's service.
            var target = string.IsNullOrWhiteSpace(url)
                ? "http://127.0.0.1:59999/probe"
                : url;

            // LOOPBACK ONLY, and this is a security control rather than a
            // convenience check.
            //
            // Left open, this endpoint takes a URL from the caller and makes
            // the server fetch it -- textbook SSRF. The Development gate on
            // this whole group is a real mitigation but not a sufficient one,
            // because Diagnostics:Enabled exists as an escape hatch and one
            // day somebody will set it in an environment that has a managed
            // identity. At that point ?url=http://169.254.169.254/metadata/...
            // turns a resilience demo into a credential-disclosure endpoint.
            //
            // The probe needs exactly one capability: fail to connect to
            // something local. Loopback covers that completely, so nothing is
            // lost by refusing everything else.
            if (!Uri.TryCreate(target, UriKind.Absolute, out var targetUri)
                || !targetUri.IsLoopback
                || (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
            {
                return Results.BadRequest(new
                {
                    error = "The probe target must be an absolute http/https loopback URL "
                          + "(127.0.0.1, localhost or [::1]). This endpoint makes the server issue "
                          + "the request, so an arbitrary target would be a server-side request "
                          + "forgery vector."
                });
            }

            var client = httpClientFactory.CreateClient(ResilienceExtensions.EntraIdClientName);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var response = await client.GetAsync(target, cancellationToken);
                stopwatch.Stop();

                return Results.Ok(new
                {
                    outcome = "completed",
                    status = (int)response.StatusCode,
                    elapsedMs = stopwatch.ElapsedMilliseconds
                });
            }
            catch (BrokenCircuitException)
            {
                // The interesting case. Note the elapsed time: this is the
                // number that makes a circuit breaker worth having.
                stopwatch.Stop();
                return Results.Ok(new
                {
                    outcome = "circuit-open",
                    status = (int?)null,
                    elapsedMs = stopwatch.ElapsedMilliseconds
                });
            }
            catch (RateLimiterRejectedException)
            {
                stopwatch.Stop();
                return Results.Ok(new
                {
                    outcome = "bulkhead-rejected",
                    status = (int?)null,
                    elapsedMs = stopwatch.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return Results.Ok(new
                {
                    outcome = ex.GetType().Name,
                    status = (int?)null,
                    elapsedMs = stopwatch.ElapsedMilliseconds
                });
            }
        });

        return app;
    }

    private record AuthorQuoteCount(string Author, int QuoteCount);
}

/// <summary>
/// Day 12 task 2 -- the row shape Dapper materializes for the hot read path.
///
/// A class with settable properties, NOT a positional record, and that is the
/// single most useful thing this exercise taught me. The first version was
/// `record AuthorQuoteRow(string Author, int QuoteCount)` and every request
/// failed with:
///
///     A parameterless default constructor or one matching signature
///     (System.String Author, System.Int64 QuoteCount) is required for
///     AuthorQuoteRow materialization
///
/// SQLite returns COUNT(*) as Int64. Dapper resolves a constructor by
/// reflection and matches parameter types exactly -- it will not narrow Int64
/// to Int32 to make a constructor fit. EF Core never hits this because it is
/// handed a model, knows the column's store type, and compiles a materializer
/// that converts.
///
/// The obvious fix -- declare `long QuoteCount` -- trades one bug for a worse
/// one: on SQL Server COUNT(*) is Int32, and Dapper will not widen Int32 to
/// Int64 for a constructor parameter either. So a record tuned to SQLite would
/// break the moment this ran against the production provider, and it would
/// break at runtime, in the hot path, not at compile time.
///
/// Settable properties avoid the whole problem because Dapper's property path
/// DOES coerce, so Int64 or Int32 both land in an int property. That is also
/// why most Dapper code in the wild uses mutable DTOs rather than records --
/// not style, but the mapper's actual contract.
/// </summary>
public sealed class AuthorQuoteRow
{
    public string Author { get; set; } = "";

    public int QuoteCount { get; set; }
}

/// <summary>
/// Day 12 task 2, part 2 -- a deliberately WIDE row, for the half of the
/// EF-vs-Dapper comparison where materialization is meant to dominate.
///
/// Settable properties for the same reason AuthorQuoteRow has them: Dapper's
/// constructor mapping matches parameter types exactly and will not coerce,
/// while its property mapping will. Both the EF and the Dapper endpoint
/// project into this same type, so neither side gains an advantage from a
/// different object shape.
/// </summary>
public sealed class QuoteWideRow
{
    public int Id { get; set; }

    public string Author { get; set; } = "";

    public string Text { get; set; } = "";

    public string? CreatedByUserId { get; set; }

    public string BackgroundImageUrl { get; set; } = "";
}
