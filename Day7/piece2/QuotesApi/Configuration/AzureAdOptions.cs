namespace QuotesApi.Configuration;

/// <summary>
/// The Azure Entra ID application registration this API trusts, bound from
/// the "AzureAd" section.
///
/// Unlike JwtOptions there is nothing secret here -- an Authority URL, a
/// tenant id and a client id are all public identifiers, which is why they
/// can live in appsettings.json. What proves a caller's identity is the
/// token signature, checked against Microsoft's published keys.
///
/// Every property is optional: an installation that only uses the API's own
/// CustomJwt scheme never configures this section at all, and the
/// authentication scheme simply never matches anything.
/// </summary>
public sealed class AzureAdOptions
{
    public const string SectionName = "AzureAd";

    public string? Authority { get; init; }
    public string? Audience { get; init; }
    public string? ClientId { get; init; }
    public string? TenantId { get; init; }
}
