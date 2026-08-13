namespace QuotesApi.Models;

public class Collection
{
    public int Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string OwnerId { get; private set; } = null!;

    public List<CollectionItem> Items { get; private set; } = [];

    private Collection()
    {
    }

    public Collection(string name, string ownerId)
    {
        SetName(name);
        OwnerId = ownerId;
    }

    // addedAt comes from the caller rather than the entity reading the
    // clock itself. Entities are plain data + invariants — they don't
    // take IClock as a constructor dependency (EF has to be able to
    // materialize them, and "what time is it" isn't a domain concern).
    // The application layer (the endpoint below) resolves IClock via DI
    // and passes the instant in, which is also what makes this testable
    // with a fixed value instead of "assert it's approximately now".
    public void AddItem(int quoteId, DateTimeOffset addedAt)
    {
        if (Items.Count >= 50)
            throw new InvalidOperationException(
                "A collection cannot contain more than 50 items.");

        if (Items.Any(x => x.QuoteId == quoteId))
            throw new InvalidOperationException(
                "This quote is already in the collection.");

        Items.Add(new CollectionItem(quoteId, addedAt));
    }

    public void RemoveItem(int quoteId)
    {
        var item = Items.FirstOrDefault(x => x.QuoteId == quoteId);

        if (item is null)
            throw new KeyNotFoundException(
                "Quote is not in the collection.");

        Items.Remove(item);
    }

    private void SetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Length < 3 ||
            name.Length > 80)
        {
            throw new ArgumentException(
                "Collection name must be between 3 and 80 characters.");
        }

        Name = name;
    }
}

public class CollectionItem
{
    public int QuoteId { get; private set; }
    public DateTime AddedAt { get; private set; }

    private CollectionItem()
    {
    }

    public CollectionItem(int quoteId, DateTimeOffset addedAt)
    {
        QuoteId = quoteId;
        AddedAt = addedAt.UtcDateTime;
    }
}