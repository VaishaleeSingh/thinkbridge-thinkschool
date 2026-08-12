using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace QuotesApi.Authorization;

/// <summary>
/// Runs once per request, immediately after authentication succeeds and
/// before any authorization policy is evaluated. Its only job: make sure
/// every authenticated caller — whether their token came through our own
/// "CustomJwt" scheme or through "EntraId" — ends up with the SAME claim
/// shape for permissions, namely one or more "scope" claims like
/// "quotes.read".
///
/// Why this is needed: a token we issue ourselves can carry a "scope"
/// claim directly, because we control how it's built. Entra ID does not
/// use that claim name. Delegated (user-context) permissions arrive as a
/// single "scp" claim holding a space-separated string, e.g.
/// "quotes.read quotes.write". Application-only permissions arrive as one
/// or more separate "roles" claims. Without this translation step, every
/// policy in InfrastructureExtensions.cs (RequireClaim("scope", ...))
/// would need to know which scheme authenticated the caller and check a
/// different claim type depending on the answer. Doing the translation
/// once, here, keeps the policies themselves simple and scheme-agnostic.
/// </summary>
public class ScopeClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = principal.Identity as ClaimsIdentity;
        if (identity is null || !identity.IsAuthenticated)
            return Task.FromResult(principal);

        // Our own CustomJwt tokens already carry "scope" claims directly —
        // nothing to translate for those.
        if (identity.HasClaim(claim => claim.Type == "scope"))
            return Task.FromResult(principal);

        // Entra ID delegated permissions: one claim, space-separated
        // values — e.g. "quotes.read quotes.write".
        var scpClaim = identity.FindFirst("scp");
        if (scpClaim is not null)
        {
            foreach (var value in scpClaim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                identity.AddClaim(new Claim("scope", value));
        }

        // Entra ID application permissions: already one claim per role,
        // just under a different claim type name than ours.
        foreach (var roleClaim in identity.FindAll("roles"))
            identity.AddClaim(new Claim("scope", roleClaim.Value));

        return Task.FromResult(principal);
    }
}
