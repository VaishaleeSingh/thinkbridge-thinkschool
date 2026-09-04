using QuotesPlatform.SharedKernel;

namespace QuotesPlatform.Modules.Publishing.Domain;

/// <summary>
/// An immutable snapshot of a collection at the moment it was approved.
///
/// It has no setters and no mutating methods on purpose. Publishing's whole
/// job is to hold something that cannot change under a reader, so the type
/// enforces it rather than relying on nobody writing a Revise method later.
/// A correction produces a NEW edition with the next number; it never edits
/// this one.
///
/// Built entirely from the CollectionPublished payload -- Publishing never
/// calls back into Curation, because an edition assembled from "the collection
/// as it is now" would not be the edition that was approved.
/// </summary>
public sealed class Edition : AggregateRoot<Guid>
{
    private readonly List<EditionItem> _items = [];

    private Edition()
    {
        Name = null!;
        Slug = null!;
        OwnerId = null!;
    }

    private Edition(
        Guid collectionId,
        int editionNumber,
        string name,
        string slug,
        string ownerId,
        DateTimeOffset publishedAt)
    {
        Id = Guid.NewGuid();
        CollectionId = collectionId;
        EditionNumber = editionNumber;
        Name = name;
        Slug = slug;
        OwnerId = ownerId;
        PublishedAt = publishedAt;
    }

    public Guid CollectionId { get; private set; }

    public int EditionNumber { get; private set; }

    public string Name { get; private set; }

    /// <summary>
    /// Stable per collection, not per edition: a reader's bookmark has to
    /// survive the next edition, so the slug addresses the collection and the
    /// edition number addresses the version behind it.
    /// </summary>
    public string Slug { get; private set; }

    public string OwnerId { get; private set; }

    public DateTimeOffset PublishedAt { get; private set; }

    public IReadOnlyList<EditionItem> Items => _items;

    public static Edition FromSnapshot(
        Guid collectionId,
        int editionNumber,
        string name,
        string slug,
        string ownerId,
        DateTimeOffset publishedAt,
        IEnumerable<EditionItem> items)
    {
        var edition = new Edition(collectionId, editionNumber, name, slug, ownerId, publishedAt);
        edition._items.AddRange(items.OrderBy(i => i.Position));

        if (edition._items.Count == 0)
            throw new DomainException("An edition cannot be published with no items.");

        return edition;
    }
}

public sealed record EditionItem(int Position, Guid QuoteId, string Author, string Text);
