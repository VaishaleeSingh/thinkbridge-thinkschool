namespace QuotesApi.Models;

/// <summary>
/// Day 12 — the READ side of the collections feature.
///
/// These types are deliberately not entities, have no invariants, no private
/// setters, no behaviour, and no place in QuotesDbContext's model. They exist
/// only to be the shape a specific screen needs. That is the whole idea: the
/// write model is shaped by the rules the data must obey, the read model is
/// shaped by the question the screen asks.
///
/// WHY TWO OF THEM AND NOT ONE
/// The old code had a single read shape (CollectionWithQuotes) used by both the
/// list endpoint and the detail endpoint. That forced the list screen to pay
/// for data it never shows: every quote's full Text, for every collection the
/// caller owns. A list of 15 collections × up to 50 quotes each is up to 750
/// full quote bodies transferred to render 15 rows of "name · 12 quotes".
///
/// So there are two read models, one per screen, because the two screens ask
/// genuinely different questions:
///   - the list asks "what collections do I have, and how big are they?"
///   - the detail asks "what is actually inside this one?"
/// A read model shared between two screens tends to become the union of both
/// their needs, which means it over-fetches for whichever screen needs less.
/// </summary>

/// <summary>
/// One row of the "my collections" list screen.
///
/// Denormalized on purpose: QuoteCount and LastAddedAt are aggregates the
/// database computes and flattens onto the row, rather than facts the client
/// derives by counting a nested array it had to be sent first. Nothing here
/// requires loading a Collection aggregate or its items.
/// </summary>
public sealed record CollectionListItem(
    int Id,
    string Name,
    int QuoteCount,
    DateTime? LastAddedAt);

/// <summary>
/// The "open one collection" detail screen.
///
/// Carries the quotes, because this is the screen that shows them — and
/// carries AddedAt, which the previous shared read shape silently dropped.
/// A detail screen that wants to show "added 3 days ago" could not, because
/// CollectionWithQuotes projected the quote but not the item's own timestamp.
/// That omission is a good illustration of the cost of a read shape that is
/// not driven by a specific screen: nobody noticed the field was missing
/// because no screen was in charge of it.
/// </summary>
public sealed record CollectionDetail(
    int Id,
    string Name,
    int QuoteCount,
    IReadOnlyList<CollectionQuote> Quotes);

/// <summary>
/// A quote as it appears inside a collection — the quote's own fields plus
/// when it was added to <em>this</em> collection. The AddedAt belongs to the
/// relationship, not to the quote, which is exactly why a projection is the
/// right tool: it can flatten fields from two tables into one shape without
/// either entity having to know about the other.
/// </summary>
public sealed record CollectionQuote(
    int QuoteId,
    string Author,
    string Text,
    DateTime AddedAt);
