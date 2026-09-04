namespace QuotesPlatform.Contracts;

public sealed record QuoteSubmitted(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid QuoteId,
    string SubmittedByUserId) : IIntegrationEvent;

/// <summary>
/// A quote cleared review and may now appear in a published edition. Curation
/// keeps a local flag from this, so the rule "a collection cannot be submitted
/// while it holds a non-publishable quote" is checked inside the aggregate
/// rather than by a synchronous call into Catalog.
/// </summary>
public sealed record QuotePublishable(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid QuoteId) : IIntegrationEvent;

/// <summary>
/// The canonical text changed -- a typo fix, a corrected attribution.
/// Consumed by Curation for DRAFT collections only; published editions keep the
/// text as published. See flow 2 in the design: this is a decision, not a bug.
/// </summary>
public sealed record QuoteRevised(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid QuoteId,
    string Author,
    string Text) : IIntegrationEvent;
