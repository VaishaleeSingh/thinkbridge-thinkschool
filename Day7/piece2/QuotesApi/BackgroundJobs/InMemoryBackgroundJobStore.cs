using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using QuotesApi.Services;

namespace QuotesApi.BackgroundJobs;

public sealed class InMemoryBackgroundJobStore : IBackgroundJobStore
{
    private readonly ConcurrentDictionary<Guid, BackgroundJobSnapshot> _jobs = new();
    private readonly IClock _clock;
    private readonly TimeSpan _retention;

    public InMemoryBackgroundJobStore(
        IClock clock,
        IOptions<BackgroundJobQueueOptions> options)
    {
        _clock = clock;
        _retention = TimeSpan.FromMinutes(options.Value.StatusRetentionMinutes);
    }

    public bool TryCreate(QuoteAuthorReportJob job)
    {
        RemoveExpiredTerminalJobs();

        return _jobs.TryAdd(
            job.Id,
            new BackgroundJobSnapshot(
                job.Id,
                nameof(QuoteAuthorReportJob),
                job.RequestedBy,
                BackgroundJobStatus.Queued,
                _clock.UtcNow));
    }

    public bool TryGet(Guid id, out BackgroundJobSnapshot? snapshot) =>
        _jobs.TryGetValue(id, out snapshot);

    public bool TryRemove(Guid id) => _jobs.TryRemove(id, out _);

    public bool TryMarkRunning(Guid id) =>
        TryTransition(
            id,
            BackgroundJobStatus.Queued,
            current => current with
            {
                Status = BackgroundJobStatus.Running,
                StartedAt = _clock.UtcNow
            });

    public bool TryMarkSucceeded(Guid id, QuoteAuthorReportResult result) =>
        TryTransition(
            id,
            BackgroundJobStatus.Running,
            current => current with
            {
                Status = BackgroundJobStatus.Succeeded,
                CompletedAt = _clock.UtcNow,
                Result = result
            });

    public bool TryMarkFailed(Guid id) =>
        TryTransition(
            id,
            BackgroundJobStatus.Running,
            current => current with
            {
                Status = BackgroundJobStatus.Failed,
                CompletedAt = _clock.UtcNow,
                Error = "The background job failed. Check server logs with the job id."
            });

    public bool TryMarkCancelled(Guid id) =>
        TryTransition(
            id,
            BackgroundJobStatus.Running,
            current => current with
            {
                Status = BackgroundJobStatus.Cancelled,
                CompletedAt = _clock.UtcNow,
                Error = "The background job was cancelled during application shutdown."
            });

    private bool TryTransition(
        Guid id,
        BackgroundJobStatus expectedStatus,
        Func<BackgroundJobSnapshot, BackgroundJobSnapshot> transition)
    {
        while (_jobs.TryGetValue(id, out var current))
        {
            if (current.Status != expectedStatus)
                return false;

            if (_jobs.TryUpdate(id, transition(current), current))
                return true;
        }

        return false;
    }

    private void RemoveExpiredTerminalJobs()
    {
        var cutoff = _clock.UtcNow - _retention;

        foreach (var pair in _jobs)
        {
            if (pair.Value.CompletedAt is { } completedAt && completedAt < cutoff)
                _jobs.TryRemove(pair.Key, out _);
        }
    }
}
