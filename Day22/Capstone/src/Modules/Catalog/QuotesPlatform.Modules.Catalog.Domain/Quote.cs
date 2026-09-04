using QuotesPlatform.SharedKernel;

namespace QuotesPlatform.Modules.Catalog.Domain;

/// <summary>
/// The canonical quote. Catalog owns the text; Curation owns snapshots of it
/// (see the design's note on why the same word means two things in two
/// contexts).
///
/// The validation rules are carried from
/// Day7/piece2/QuotesApi/Models/Quote.cs, where they live in a Create factory
/// for a reason worth repeating: before the factory existed those checks were
/// inline in one POST handler, so nothing stopped an import job or an admin
/// tool from constructing an invalid Quote with an object initializer.
/// </summary>
public sealed class Quote : AggregateRoot<Guid>
{
    public const int MaxAuthorLength = 200;
    public const int MaxTextLength = 1000;

    private Quote()
    {
        Author = null!;
        Text = null!;
    }

    private Quote(string author, string text, string submittedByUserId, DateTimeOffset createdAt)
    {
        Id = Guid.NewGuid();
        Author = author;
        Text = text;
        SubmittedByUserId = submittedByUserId;
        CreatedAt = createdAt;
        IsPublishable = false;
    }

    public string Author { get; private set; }

    public string Text { get; private set; }

    public string? SubmittedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// False until Moderation approves it. Curation mirrors this flag from the
    /// QuotePublishable event so it can enforce "nothing unreviewed goes out in
    /// an edition" without a synchronous call into this module.
    /// </summary>
    public bool IsPublishable { get; private set; }

    public static Quote Submit(
        string author,
        string text,
        string submittedByUserId,
        DateTimeOffset createdAt) =>
        new(Validate(author, MaxAuthorLength, "Author"),
            Validate(text, MaxTextLength, "Text"),
            submittedByUserId,
            createdAt);

    public void MarkPublishable() => IsPublishable = true;

    /// <summary>
    /// A correction to canonical text. Reaches drafts and stops at published
    /// editions -- see flow 2 in the design; the rule lives in Curation's
    /// aggregate, not here.
    /// </summary>
    public void Revise(string author, string text)
    {
        Author = Validate(author, MaxAuthorLength, "Author");
        Text = Validate(text, MaxTextLength, "Text");
    }

    private static string Validate(string value, int maxLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException($"{field} is required.");

        var trimmed = value.Trim();

        return trimmed.Length > maxLength
            ? throw new DomainException($"{field} must be {maxLength} characters or less.")
            : trimmed;
    }
}
