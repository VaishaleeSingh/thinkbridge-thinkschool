using QuotesPlatform.SharedKernel;

namespace QuotesPlatform.Modules.Curation.Domain;

/// <summary>
/// Internal to this module. The Application layer translates these into the
/// integration events in QuotesPlatform.Contracts -- the domain never
/// references that project, so it cannot publish a cross-module fact from
/// inside an entity, before the transaction that makes it true has committed.
/// </summary>
public sealed record CollectionSubmittedForReview(
    Guid CollectionId,
    string OwnerId,
    string Name,
    int ItemCount,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record CollectionEditionPublished(
    Guid CollectionId,
    int EditionNumber,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record CollectionReviewRejected(
    Guid CollectionId,
    string Reason,
    DateTimeOffset OccurredAt) : IDomainEvent;
