namespace QuotesApi.Models;

/// <summary>
/// One row per QuoteChanged event processed by the "audit" subscription.
/// Proves that the event was received and recorded, even if it was delivered
/// more than once (the ProcessedMessages dedupe store prevents duplicate rows).
/// </summary>
public sealed class QuoteAuditEntry
{
    public int Id { get; set; }
    public required string EventId { get; set; }
    public required int QuoteId { get; set; }
    public required string EventType { get; set; }
    public string? OwnerId { get; set; }
    public required DateTimeOffset OccurredAt { get; set; }
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
}
