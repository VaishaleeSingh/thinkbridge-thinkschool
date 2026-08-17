using System.IdentityModel.Tokens.Jwt;

namespace QuotesApi.Authorization;

/// <summary>
/// Decides which authentication scheme -- "CustomJwt" or "EntraId" -- should
/// validate an incoming request's bearer token, by peeking at the token's
/// "aud" (audience) claim WITHOUT validating it. Entra ID always issues
/// audiences shaped like "api://something", while our own tokens use a
/// plain string ("quotes-api"); that one difference is enough to route
/// correctly. Real validation still happens afterward, inside whichever
/// scheme this selects.
///
/// This used to live inline as the ForwardDefaultSelector lambda passed to
/// AddPolicyScheme(...) in InfrastructureExtensions. Pulled out into its own
/// class specifically so this decision can be unit tested directly: as an
/// inline lambda, the only way to exercise the "route to EntraId" branch was
/// a real HTTP request through the full host, which would then also depend
/// on reaching Microsoft's real identity endpoint over the network just to
/// finish the request -- an unwanted dependency for what is, underneath,
/// a pure function of one header value. As a standalone static method, all
/// of its branches -- no header, malformed token, CustomJwt-shaped
/// audience, EntraId-shaped audience -- are testable with no running host
/// and no network call at all.
/// </summary>
public static class AuthSchemeSelector
{
    public const string CustomJwtScheme = "CustomJwt";
    public const string EntraIdScheme = "EntraId";

    public static string Select(string? authorizationHeaderValue)
    {
        // No token at all -> nothing to inspect. Forward to CustomJwt
        // anyway; it will correctly reject the request with 401 since
        // there's no token to validate.
        if (string.IsNullOrEmpty(authorizationHeaderValue))
            return CustomJwtScheme;

        try
        {
            var rawToken = authorizationHeaderValue.Replace("Bearer ", "").Trim();
            var tokenReader = new JwtSecurityTokenHandler();

            // CanReadToken/ReadToken only parse the token's structure --
            // they do NOT check the signature. Real validation still
            // happens afterward, inside whichever scheme we forward to.
            if (tokenReader.CanReadToken(rawToken))
            {
                var parsedToken = tokenReader.ReadToken(rawToken) as JwtSecurityToken;
                var audience = parsedToken?.Claims
                    .FirstOrDefault(claim => claim.Type == "aud")?.Value ?? "";

                if (audience.Contains("api://"))
                    return EntraIdScheme;
            }
        }
        catch
        {
            // Token is malformed / not a JWT at all. Fall through to
            // CustomJwt below, which will reject it with 401 through the
            // normal validation pipeline.
        }

        return CustomJwtScheme;
    }
}
