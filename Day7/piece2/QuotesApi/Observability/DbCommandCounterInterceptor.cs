using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using QuotesApi.Caching;

namespace QuotesApi.Observability;

/// <summary>
/// Counts database commands as EF executes them, grouped by query family.
///
/// THIS IS THE INSTRUMENT THE DAY 21 TASK ACTUALLY ASKS FOR. "Measure the DB
/// load drop" is a claim about the database, and no amount of cache-side
/// counting establishes it: a 99% hit rate is perfectly consistent with the
/// database still being hammered through some other path. Counting at the
/// point of execution is the only place the answer exists.
///
/// It is also why this was built and the baseline recorded BEFORE the cache
/// existed. A before/after number reconstructed after the fact is not a
/// measurement, it is a memory.
///
/// HOW A COMMAND IS CLASSIFIED, AND WHY NOT BY SQL TEXT:
/// QuoteRepository tags its list query with EF's TagWith, which emits a SQL
/// comment ("-- quotes-list") ahead of the statement. Matching on that tag is
/// still string matching, but on a string WE control and that changes only when
/// someone means it to. Matching on the shape of the generated SQL -- looking
/// for "COUNT(*)" and "LIMIT", say -- breaks the first time the query is
/// touched, and breaks silently, by reclassifying commands as "other" and
/// making the headline number look better than it is.
///
/// Registered as a singleton and attached in AddDbContext. The test factories
/// re-register the DbContext, so they have to attach it too -- see
/// QuotesApiFactory. An interceptor that is silently absent produces a count of
/// zero, which reads exactly like a perfect cache.
/// </summary>
public sealed class DbCommandCounterInterceptor : DbCommandInterceptor, IDisposable
{
    public const string MeterName = "QuotesApi.Database";
    public const string OtherFamily = "other";

    private readonly Meter _meter;
    private readonly Counter<long> _commands;
    private readonly ConcurrentDictionary<string, long> _counts = new();

    public DbCommandCounterInterceptor()
    {
        _meter = new Meter(MeterName);

        _commands = _meter.CreateCounter<long>(
            "db.commands", "commands", "Database commands executed, tagged by query family.");
    }

    /// <summary>Counts by family since process start, or since <see cref="Reset"/>.</summary>
    public IReadOnlyDictionary<string, long> Snapshot() =>
        _counts.ToDictionary(pair => pair.Key, pair => pair.Value);

    public long CountFor(string family) =>
        _counts.TryGetValue(family, out var count) ? count : 0;

    /// <summary>
    /// TEST-ONLY. The stampede test needs a zero baseline, and asserting on a
    /// delta would hide the case where the count is already wrong before the
    /// load starts. Not exposed through any endpoint: a counter an operator can
    /// zero is a counter nobody can trust.
    /// </summary>
    public void Reset() => _counts.Clear();

    private void Count(DbCommand command)
    {
        var family = Classify(command.CommandText);

        _counts.AddOrUpdate(family, 1, static (_, existing) => existing + 1);
        _commands.Add(1, new KeyValuePair<string, object?>("family", family));
    }

    private static string Classify(string? commandText) =>
        commandText is not null && commandText.Contains(CacheKeys.QuoteListQueryTag, StringComparison.Ordinal)
            ? CacheKeys.QuoteListFamily
            : OtherFamily;

    // Every execution path is overridden. Missing one -- ScalarExecuting is the
    // easy one to forget, and it is what a COUNT(*) goes through on some
    // providers -- would undercount exactly the query being measured.

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Count(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Count(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Count(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Count(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Count(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Count(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public void Dispose() => _meter.Dispose();
}
