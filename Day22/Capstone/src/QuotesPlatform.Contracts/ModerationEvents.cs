namespace QuotesPlatform.Contracts;

public sealed record CollectionApproved(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid CollectionId,
    string ReviewerId) : IIntegrationEvent;

/// <summary>
/// Reason is required, and that is a product decision with teeth: a rejection
/// a curator cannot act on wastes the review round trip entirely.
/// </summary>
public sealed record CollectionRejected(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid CollectionId,
    string ReviewerId,
    string Reason) : IIntegrationEvent;

public sealed record QuoteApproved(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid QuoteId,
    string ReviewerId) : IIntegrationEvent;
