namespace QuotesApi.BackgroundJobs;

public interface IBackgroundJobQueue
{
    bool TryEnqueue(QuoteAuthorReportJob job);

    ValueTask<QuoteAuthorReportJob> DequeueAsync(CancellationToken cancellationToken);
}
