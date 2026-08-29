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

    public const string DefaultProxyAppRole = "Quotes.Proxy";

    public string? Authority { get; init; }
    public string? Audience { get; init; }
    public string? ClientId { get; init; }
    public string? TenantId { get; init; }

    /// <summary>
    /// The app role a caller must hold before this API will honour the user
    /// identity it forwards in <c>X-Forwarded-Authorization</c>. Defaults to
    /// <c>Quotes.Proxy</c> -- the role defined on the API's app registration
    /// and granted to the Day 17 BFF's managed identity.
    /// </summary>
    public string? ProxyAppRole { get; init; }

    /// <summary>
    /// The object id (<c>oid</c>) of the one principal allowed to forward a
    /// user identity -- the BFF's managed identity.
    ///
    /// <para>
    /// Not a secret: an object id is a public identifier, and holding it
    /// grants nothing. It is a pin, not a credential. The role check alone
    /// would let any application in the tenant that was ever granted
    /// <c>Quotes.Proxy</c> impersonate any user; this narrows that to one
    /// principal. Left unset, no forwarded identity is accepted at all --
    /// see ForwardedUserAuthentication for why unconfigured means closed.
    /// </para>
    /// </summary>
    public string? ProxyObjectId { get; init; }
}
