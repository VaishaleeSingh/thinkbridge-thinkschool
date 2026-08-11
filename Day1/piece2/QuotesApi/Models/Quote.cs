namespace QuotesApi.Models;

/// <summary>
/// Rich domain model for Quote. Enforces invariants:
/// - Author: 1-200 chars
/// - Text: 1-1000 chars
/// - Text is immutable after creation (soft-deleted via flag instead)
///
/// Construction only via Quote.Create() factory method, which validates
/// and returns either a Quote or a domain error.
/// </summary>
public class Quote
{
    public int Id { get; private set; }
    public string Author { get; private set; } = null!;
    public string Text { get; private set; } = null!;
    public bool IsDeleted { get; private set; }

    private Quote()
    {
    }

    /// <summary>
    /// Factory method to create a Quote with validation.
    /// Returns either the created quote or a validation error.
    /// </summary>
    public static (Quote? Quote, string? Error) Create(string author, string text)
    {
        if (string.IsNullOrWhiteSpace(author))
            return (null, "Author is required.");

        if (string.IsNullOrWhiteSpace(text))
            return (null, "Text is required.");

        if (author.Length > 200)
            return (null, "Author must be 200 characters or less.");

        if (text.Length > 1000)
            return (null, "Text must be 1000 characters or less.");

        if (author.Length < 1)
            return (null, "Author must be at least 1 character.");

        if (text.Length < 1)
            return (null, "Text must be at least 1 character.");

        return (new Quote { Author = author, Text = text }, null);
    }

    /// <summary>
    /// Soft-delete the quote. Text remains immutable; only the flag changes.
    /// </summary>
    public void Delete()
    {
        IsDeleted = true;
    }
}