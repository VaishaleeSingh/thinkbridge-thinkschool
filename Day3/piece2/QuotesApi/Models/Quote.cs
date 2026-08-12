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
}
