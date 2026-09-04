namespace QuotesPlatform.Contracts;

/// <summary>Curation asks for a review. Moderation opens one.</summary>
public sealed record CollectionSubmittedForPublication(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid CollectionId,
    string OwnerId,
    string Name,
    int ItemCount) : IIntegrationEvent;

/// <summary>
/// Curation announces a published edition, CARRYING THE FULL SNAPSHOT.
///
/// The payload is fat on purpose. If Publishing had to call back into Curation
/// for the items, the edition it built would reflect the collection as it is
/// NOW rather than as it was approved -- and the whole point of an edition is
/// that it is a fixed thing readers can be shown.
/// </summary>
public sealed record CollectionPublished(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid CollectionId,
    int EditionNumber,
    string Name,
    string OwnerId,
    IReadOnlyList<PublishedItem> Items) : IIntegrationEvent;

/// <summary>
/// One item as it appeared when the edition was published. Author and Text are
/// snapshots, not references: a later correction in Catalog does not rewrite a
/// published edition. See flow 2 in the design.
/// </summary>
public sealed record PublishedItem(int Position, Guid QuoteId, string Author, string Text);
