using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using QuotesApi.Data;
using QuotesApi.Messaging.Outbox;
using QuotesApi.Models;
using QuotesApi.Services;

namespace QuotesApi.Extensions;

/// <summary>
/// Wires the transactional outbox. Deliberately separate from
/// MessagingExtensions, because the two are switched independently and for
/// different reasons.
///
/// The WRITER is always registered. An outbox row is part of the domain
/// transaction, not part of messaging: a process that is not allowed to
/// publish must still record what it committed, or the whole guarantee is
/// conditional on a setting.
///
/// The RELAY is registered only when Outbox:RelayEnabled is true. That keeps
/// the existing test suite meaningful -- with the relay off, a test can assert
/// that a Pending row exists and nothing drained it -- and it makes
/// "write here, publish there" a configuration, which is what you want the
/// moment a second process (a worker, a migration host) shares this database.
/// </summary>
public static class OutboxExtensions
{
    public static IServiceCollection AddOutbox(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Scoped: must share the caller's DbContext, or the row leaves the
        // transaction it exists to be part of. See EfOutboxWriter.
        services.AddScoped<IOutboxWriter, EfOutboxWriter>();

        // Scoped for the same reason -- it owns the transaction.
        services.AddScoped<IQuoteWriteService, QuoteWriteService>();

        // Singleton: one wake-up channel shared by every request and the relay.
        services.AddSingleton<IOutboxSignal, ChannelOutboxSignal>();

        // Singleton, and registered even when the relay is off: the meter is
        // created once per process, and OutboxMetrics is resolved by the
        // diagnostics endpoint as well as the relay.
        services.AddSingleton<OutboxMetrics>();

        var options = configuration
            .GetSection(OutboxOptions.SectionName)
            .Get<OutboxOptions>() ?? new OutboxOptions();

        if (options.RelayEnabled)
        {
            services.AddHostedService<OutboxRelayService>();
            services.AddHostedService<OutboxRetentionService>();
        }

        return services;
    }

    /// <summary>
    /// GET /api/outbox/status -- counts by status, the oldest pending age, and
    /// the most recent parked rows.
    ///
    /// Mapped unconditionally rather than behind the Development-only
    /// diagnostics guard, because this is the endpoint an operator needs in
    /// production at the exact moment they suspect the relay has stopped.
    /// It requires authentication, returns counts rather than payloads, and
    /// reports LastError, which by construction carries an exception type and
    /// message and never event content.
    /// </summary>
    public static IEndpointRouteBuilder MapOutboxEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/outbox/status", async (
            QuotesDbContext db,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            // ONE query for both the counts and the oldest pending row.
            //
            // It was two, and they contradicted each other in the Day 20 crash
            // run: the same response reported Pending = 1 and
            // oldestPendingUtc = null. Reproducing each statement against this
            // schema by hand shows both are correct SQL that returns what it
            // should, so the fault was never in either query -- it was in
            // asking the same question twice and publishing both answers as
            // though they described one moment.
            //
            // Aggregated per status in a single GROUP BY, the count and the
            // minimum OccurredAtUtc come from the same rows in the same scan.
            // They can now be stale together, which is honest, but they cannot
            // disagree, which is what made the old response untrustworthy at
            // exactly the moment an operator would be reading it.
            var summary = await db.OutboxMessages
                .AsNoTracking()
                .GroupBy(m => m.Status)
                .Select(g => new
                {
                    Status = g.Key,
                    Count = g.Count(),
                    OldestOccurredAtUtc = g.Min(m => m.OccurredAtUtc)
                })
                .ToListAsync(cancellationToken);

            var pending = summary.SingleOrDefault(s => s.Status == OutboxStatus.Pending);

            // Null only when there is no Pending group at all -- which is the
            // same fact as a Pending count of zero, from the same row set.
            DateTime? oldestPending = pending is null ? null : pending.OldestOccurredAtUtc;

            var parked = await db.OutboxMessages
                .AsNoTracking()
                .Where(m => m.Status == OutboxStatus.Failed)
                .OrderByDescending(m => m.Id)
                .Take(10)
                .Select(m => new
                {
                    m.Id,
                    m.MessageId,
                    m.EventType,
                    m.Attempts,
                    m.LastError
                })
                .ToListAsync(cancellationToken);

            return Results.Ok(new
            {
                counts = summary.ToDictionary(x => x.Status, x => x.Count),
                pendingCount = pending?.Count ?? 0,
                oldestPendingUtc = oldestPending,

                // The number to alert on. A pending count is spiky by nature;
                // a row that has been pending for minutes means the relay is
                // dead, and that is the only way this design stops delivering
                // without anything raising an error.
                oldestPendingAgeSeconds = oldestPending is null
                    ? 0
                    : Math.Max(0, (clock.UtcNow.UtcDateTime - oldestPending.Value).TotalSeconds),
                parked
            });
        }).RequireAuthorization();

        return app;
    }
}
