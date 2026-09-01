namespace QuotesApi.Models;

/// <summary>
/// A denormalised row kept in sync by the "search-index" subscription.
/// Only Created and Updated events reach this handler (subscription filter
/// excludes Deleted), so this row is upserted but never deleted here.
/// </summary>
public sealed class QuoteSearchProjection
{
    public int QuoteId { get; set; }
    public string? Author { get; set; }
    public string? Text { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; }
}
