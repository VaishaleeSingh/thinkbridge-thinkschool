using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Services;

namespace QuotesApi.Messaging.Outbox;

/// <summary>
/// Deletes Sent outbox rows, and processed-message rows, once they are older
/// than the retention window.
///
/// Both tables grow forever without this. Day 19 wrote that down for
/// ProcessedMessages and did not fix it ("rows grow forever without a cleanup
/// job"); the outbox would have added a second table with the same problem, so
/// one sweep handles both.
///
/// THE WINDOW HAS A FLOOR, and it is not a matter of taste: it must exceed
/// message TTL plus the longest plausible dead-letter dwell time. A dedupe row
/// swept while its message can still be replayed means the replay looks new,
/// the handler runs again, and the side effect repeats -- a silent failure of
/// the guarantee the outbox depends on. Shortening RetentionDays to save space
/// is therefore a correctness change, not a housekeeping one.
///
/// Sent rows are deleted; Failed rows are NOT. A parked row is the record that
/// an event was never delivered, and it is the only such record -- deleting it
/// on a timer would destroy the evidence of the incident it represents.
/// </summary>
public sealed class OutboxRetentionService(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    IClock clock,
    ILogger<OutboxRetentionService> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Outbox retention sweep started (every {SweepInterval}, keeping {RetentionDays} days)",
            _options.RetentionSweepInterval, _options.RetentionDays);

        // One interval before the first sweep, deliberately. Startup is the
        // busiest and least predictable moment in a process's life, and a
        // delete over two tables is the last thing that should compete with
        // migrations and the first requests.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.RetentionSweepInterval, stoppingToken);
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Outbox retention sweep failed. Will try again next interval.");
            }
        }
    }

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.UtcDateTime.AddDays(-_options.RetentionDays);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();

        var outboxDeleted = await db.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Sent
                        && m.SentAtUtc != null
                        && m.SentAtUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var processedDeleted = await db.ProcessedMessages
            .Where(m => m.ProcessedAtUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (outboxDeleted > 0 || processedDeleted > 0)
        {
            logger.LogInformation(
                "Retention sweep removed {OutboxDeleted} sent outbox rows and {ProcessedDeleted} processed-message rows older than {Cutoff:u}",
                outboxDeleted, processedDeleted, cutoff);
        }
    }
}
