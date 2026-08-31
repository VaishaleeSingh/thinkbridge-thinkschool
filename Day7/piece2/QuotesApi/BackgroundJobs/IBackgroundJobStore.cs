namespace QuotesApi.BackgroundJobs;

public interface IBackgroundJobStore
{
    bool TryCreate(QuoteAuthorReportJob job);

    bool TryGet(Guid id, out BackgroundJobSnapshot? snapshot);

    bool TryRemove(Guid id);

    bool TryMarkRunning(Guid id);

    bool TryMarkSucceeded(Guid id, QuoteAuthorReportResult result);

    bool TryMarkFailed(Guid id);

    bool TryMarkCancelled(Guid id);
}
