namespace QuotesApi.BackgroundJobs;

public enum BackgroundJobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public sealed record QuoteAuthorReportJob(
    Guid Id,
    int TopAuthors,
    string RequestedBy);

public sealed record QuoteAuthorCount(string Author, int QuoteCount);

public sealed record QuoteAuthorReportResult(
    int TotalQuotes,
    int DistinctAuthors,
    IReadOnlyList<QuoteAuthorCount> TopAuthors);

public sealed record BackgroundJobSnapshot(
    Guid Id,
    string JobType,
    string RequestedBy,
    BackgroundJobStatus Status,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    QuoteAuthorReportResult? Result = null,
    string? Error = null);
