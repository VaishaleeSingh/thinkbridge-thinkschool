using System.ComponentModel.DataAnnotations;

namespace QuotesApi.Configuration;

/// <summary>
/// Limits on how much a caller may ask for in one page, bound from the
/// "Pagination" section. The maximum used to be a bare 100 written into the
/// quote listing endpoint's validation.
///
/// This is the one place in this app where IOptionsSnapshot is genuinely
/// the right lifetime rather than a demonstration (see where it is
/// injected): a page-size ceiling is exactly the kind of operational dial
/// worth turning without a redeploy -- if a client starts requesting
/// 100-item pages and the database feels it, someone should be able to drop
/// the ceiling to 25 and have the next request respect it.
/// </summary>
public sealed class PaginationOptions
{
    public const string SectionName = "Pagination";

    [Range(1, 1000)]
    public int MaxPageSize { get; init; } = 100;
}
