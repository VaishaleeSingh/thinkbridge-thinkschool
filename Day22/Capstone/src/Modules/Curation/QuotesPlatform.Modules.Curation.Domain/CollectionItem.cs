using QuotesPlatform.SharedKernel;

namespace QuotesPlatform.Modules.Curation.Domain;

/// <summary>
/// A quote as it appeared when it was added to this collection.
///
/// Author and Text are SNAPSHOTS, and that is the single most consequential
/// decision in this module. Curation could hold only QuoteId and read the text
/// from Catalog when publishing -- and then a typo fix in Catalog would rewrite
/// a published edition retroactively, and publishing would need a synchronous
/// call into another module to do its job. Both are worse than the cost of
/// duplicated text.
///
/// The snapshot is refreshed from QuoteRevised while the collection is a draft,
/// and deliberately not once it is published. See flow 2 in the design.
/// </summary>
public sealed class CollectionItem : Entity<Guid>
{
    private CollectionItem()
    {
        Author = null!;
        Text = null!;
    }

    internal CollectionItem(
        Guid quoteId,
        string author,
        string text,
        bool isPublishable,
        int position,
        DateTimeOffset addedAt)
    {
        Id = Guid.NewGuid();
        QuoteId = quoteId;
        Author = author;
        Text = text;
        IsPublishable = isPublishable;
        Position = position;
        AddedAt = addedAt;
    }

    public Guid QuoteId { get; private set; }

    public string Author { get; private set; }

    public string Text { get; private set; }

    /// <summary>
    /// Kept locally from the QuotePublishable integration event, so the
    /// aggregate can enforce "nothing unreviewed goes out in an edition"
    /// without reaching into Catalog mid-transaction.
    /// </summary>
    public bool IsPublishable { get; private set; }

    /// <summary>1-based, contiguous within the collection. See Collection.Reorder.</summary>
    public int Position { get; private set; }

    public DateTimeOffset AddedAt { get; private set; }

    internal void MoveTo(int position) => Position = position;

    internal void RefreshSnapshot(string author, string text)
    {
        Author = author;
        Text = text;
    }

    internal void MarkPublishable() => IsPublishable = true;
}
