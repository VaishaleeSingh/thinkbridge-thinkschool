using System.Diagnostics;
using QuotesApi.Services;

namespace QuotesApi.BackgroundJobs;

public sealed class QueuedBackgroundJobService(
    IBackgroundJobQueue queue,
    IBackgroundJobStore store,
    IServiceScopeFactory scopeFactory,
    ILogger<QueuedBackgroundJobService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Background job worker started");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var job = await queue.DequeueAsync(stoppingToken);
                await ProcessJobAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Background job worker stopped after shutdown was requested");
        }
    }

    private async Task ProcessJobAsync(
        QuoteAuthorReportJob job,
        CancellationToken stoppingToken)
    {
        if (!store.TryMarkRunning(job.Id))
        {
            logger.LogWarning(
                "Skipped background job {JobId} because it was not in the queued state",
                job.Id);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Started background job {JobId} of type {JobType}",
            job.Id,
            nameof(QuoteAuthorReportJob));

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider
                .GetRequiredService<IQuoteAuthorReportProcessor>();

            var result = await processor.ProcessAsync(job, stoppingToken);
            store.TryMarkSucceeded(job.Id, result);

            logger.LogInformation(
                "Completed background job {JobId} in {ElapsedMilliseconds} ms",
                job.Id,
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            store.TryMarkCancelled(job.Id);
            logger.LogInformation(
                "Cancelled background job {JobId} during application shutdown after {ElapsedMilliseconds} ms",
                job.Id,
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            store.TryMarkFailed(job.Id);
            logger.LogError(
                exception,
                "Background job {JobId} failed after {ElapsedMilliseconds} ms",
                job.Id,
                stopwatch.ElapsedMilliseconds);
        }
    }
}
