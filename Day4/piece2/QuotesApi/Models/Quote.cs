namespace QuotesApi.Models;

public class Quote
{
    public int Id { get; set; }
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>
    /// Id of the user who created this quote — taken from their token's
    /// "sub" (custom JWT) or "oid"/"sub" (Entra ID) claim at the moment the
    /// quote was created. See QuoteEndpointExtensions.MapQuoteEndpoints,
    /// the POST handler.
    ///
    /// Null has a specific meaning here: it means either this quote existed
    /// before this column was added (a "legacy" quote — this project chose
    /// NOT to backfill those), or it was created by a caller with no
    /// identifiable user id. Either way, MustOwnQuoteHandler treats a null
    /// owner as "no ownership rule applies" rather than "nobody can touch
    /// this" — only quotes created after Day 3's ownership rules exist
    /// actually enforce them.
    /// </summary>
    public string? CreatedByUserId { get; set; }

    /// <summary>
    /// The one place that decides whether an author/text pair is even
    /// allowed to become a Quote. Before this factory existed, these same
    /// rules were checked by hand inside QuoteEndpointExtensions' POST
    /// handler, and nowhere else — meaning nothing stopped some OTHER
    /// caller (a background import job, a future admin tool) from building
    /// an invalid Quote just by using the object initializer directly.
    /// Putting the rule here, on the model itself, means it can never be
    /// bypassed no matter who's constructing a Quote — the same reasoning
    /// Collection already follows in its constructor.
    ///
    /// Throws <see cref="ArgumentException"/> naming the offending
    /// parameter for every failure mode, so callers (and tests) can assert
    /// exactly which field was invalid.
    /// </summary>
    public static Quote Create(string author, string text, string? createdByUserId = null)
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new ArgumentException("Author is required.", nameof(author));

        if (author.Length > 200)
            throw new ArgumentException("Author must be 200 characters or less.", nameof(author));

        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text is required.", nameof(text));

        if (text.Length > 1000)
            throw new ArgumentException("Text must be 1000 characters or less.", nameof(text));

        return new Quote
        {
            Author = author,
            Text = text,
            CreatedByUserId = createdByUserId
        };
    }
}
