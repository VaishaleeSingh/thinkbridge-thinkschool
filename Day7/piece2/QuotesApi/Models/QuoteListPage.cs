namespace QuotesApi.Models;

/// <summary>
/// The cache contract for one page of the quotes list.
///
/// A DELIBERATE DTO, not the EF entity. Caching Quote directly is the
/// shortcut that looks free and is not:
///
///   - The cache format would become the EF model. A property added for
///     persistence reasons silently changes what is serialised, and entries
///     written by the previous build deserialise into a shape the new code did
///     not expect. Nothing fails loudly; the data is just wrong.
///   - Entities come out of a cache detached but indistinguishable from tracked
///     ones, which invites someone downstream to mutate one and call
///     SaveChanges on a graph EF never loaded.
///
/// Records, so equality is by value -- which is what lets a test assert that
/// what came back from the cache is what went in, rather than comparing
/// field by field.
///
/// CacheKeys.Version is bumped whenever this shape changes, so a deploy cannot
/// read entries written against the old contract. That is cheaper and safer
/// than any migration story for cache data: the old keys simply stop being
/// addressed and expire on their own.
/// </summary>
public sealed record QuoteListPage(
    int Page,
    int Size,
    int Total,
    IReadOnlyList<QuoteListItem> Items);

/// <summary>
/// One quote as the list endpoint returns it.
///
/// Deliberately the same fields the endpoint already serialised before Day 21,
/// so the cached response is byte-identical to the uncached one -- which is an
/// assertion in QuoteListCacheTests, not an aspiration. A cache that quietly
/// changes the response shape is a breaking API change disguised as an
/// optimisation.
/// </summary>
public sealed record QuoteListItem(
    int Id,
    string Author,
    string Text,
    string BackgroundImageUrl,
    string? CreatedByUserId)
{
    public static QuoteListItem From(Quote quote) => new(
        quote.Id,
        quote.Author,
        quote.Text,
        quote.BackgroundImageUrl,
        quote.CreatedByUserId);
}
