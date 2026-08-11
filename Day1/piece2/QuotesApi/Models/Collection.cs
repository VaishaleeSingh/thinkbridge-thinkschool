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

    public void AddItem(int quoteId)
    {
        if (Items.Count >= 50)
            throw new InvalidOperationException(
                "A collection cannot contain more than 50 items.");

        if (Items.Any(x => x.QuoteId == quoteId))
            throw new InvalidOperationException(
                "This quote is already in the collection.");

        Items.Add(new CollectionItem(quoteId));
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

    public CollectionItem(int quoteId)
    {
        QuoteId = quoteId;
        AddedAt = DateTime.UtcNow;
    }
}