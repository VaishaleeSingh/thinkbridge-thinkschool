using System.IdentityModel.Tokens.Jwt;
using System.Text;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

using QuotesApi.Configuration;

namespace QuotesApi.Authorization;

/// <summary>
/// Accepts the user identity that the Day 17 BFF forwards in
/// <c>X-Forwarded-Authorization</c> -- but only on a request that has already
/// proved, with a managed-identity token, that it came from the BFF.
///
/// <para>
/// WHY THIS EXISTS. The BFF holds a managed identity and puts its app-only
/// token in <c>Authorization</c>; that is what makes "every call to this API
/// carries a managed-identity token and no secret exists" true. But an
/// app-only token has no user in it -- no <c>sub</c>, and <c>roles</c> rather
/// than the <c>scope</c> claims the endpoint policies are written against. So
/// with the BFF in the path and this class absent, every call authenticates
/// successfully as the *application* and then fails authorization: 403 on
/// every quotes and collections endpoint, and the Day 3 ownership checks in
/// MustOwnQuoteHandler and CollectionEndpointExtensions lose the one value
/// they are built on. The proxy has always sent the header
/// (ApiProxyFunction.BuildOutboundRequest); this is the half that reads it.
/// </para>
///
/// <para>
/// WHY THE GATES ARE NOT OPTIONAL. A forwarded-identity header is a request
/// asking to be treated as somebody. It is only safe when the transport
/// carrying it is itself authenticated, because otherwise anyone who can
/// reach the API can name any user they like -- a free impersonation
/// primitive, and a worse hole than the one the managed identity closed. The
/// managed-identity token is that authentication, so all three gates below
/// are checked before the header is even parsed:
/// </para>
/// <list type="number">
///   <item>the outer token is <b>app-only</b> -- no <c>scp</c>, no <c>upn</c>.
///     A delegated token means a user obtained it, not the platform.</item>
///   <item>it carries the <b>expected app role</b> (<c>Quotes.Proxy</c>).</item>
///   <item>its <c>oid</c> equals the <b>configured proxy object id</b>. Role
///     membership alone would let any application in the tenant that was ever
///     granted the role forward identities; this pins it to one principal.</item>
/// </list>
///
/// <para>
/// FAIL CLOSED, AND LOUDLY. If the header is present and any gate fails, the
/// request is rejected rather than downgraded to the app-only identity.
/// Quietly ignoring it would turn an attempted impersonation into a
/// confusing 403 somewhere further in, and would hide the attempt. If
/// <c>ProxyObjectId</c> is not configured at all, no forwarded identity is
/// ever accepted -- an unconfigured deployment is closed, not open.
/// </para>
/// </summary>
public static class ForwardedUserAuthentication
{
    public const string HeaderName = "X-Forwarded-Authorization";

    /// <summary>
    /// The validation parameters for the API's own self-issued tokens.
    ///
    /// <para>
    /// Shared with the "CustomJwt" scheme rather than written twice. The
    /// forwarded token is exactly the same kind of token the browser would
    /// have sent directly, so it has to be held to exactly the same standard
    /// -- same key, same issuer, same audience, same zero clock skew. Two
    /// copies of these parameters is how one of them ends up with
    /// ValidateLifetime relaxed "just for the proxy path".
    /// </para>
    /// </summary>
    public static TokenValidationParameters CustomJwtParameters(JwtOptions jwt) => new()
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),

        ValidateIssuer = true,
        ValidIssuer = jwt.Issuer,

        ValidateAudience = true,
        ValidAudience = jwt.Audience,

        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero,
    };

    /// <summary>
    /// Runs on the "EntraId" scheme after the managed-identity token has been
    /// validated. Leaves a genuine app-only call untouched; swaps in the
    /// forwarded user when one is present and every gate passes.
    /// </summary>
    public static Task OnTokenValidatedAsync(
        TokenValidatedContext context,
        JwtOptions jwt,
        AzureAdOptions azureAd)
    {
        var forwarded = context.Request.Headers[HeaderName].FirstOrDefault();

        // No forwarded identity: an ordinary app-only call (a health probe,
        // or a future service-to-service caller). Nothing to do -- the app
        // principal stands on its own.
        if (string.IsNullOrWhiteSpace(forwarded))
        {
            return Task.CompletedTask;
        }

        var caller = context.Principal;

        if (caller is null)
        {
            context.Fail($"{HeaderName} was present on a request with no validated principal.");
            return Task.CompletedTask;
        }

        // Gate 1 -- app-only. A delegated token carries a scope ("scp") or a
        // user principal name; either means this token represents a user who
        // obtained it, not the platform vouching for an application.
        if (caller.FindFirst("scp") is not null || caller.FindFirst("upn") is not null)
        {
            context.Fail($"{HeaderName} is only honoured on an app-only token.");
            return Task.CompletedTask;
        }

        // Gate 2 -- the expected app role.
        var requiredRole = string.IsNullOrWhiteSpace(azureAd.ProxyAppRole)
            ? AzureAdOptions.DefaultProxyAppRole
            : azureAd.ProxyAppRole;

        var hasRole = caller
            .FindAll("roles")
            .Any(claim => string.Equals(claim.Value, requiredRole, StringComparison.Ordinal));

        if (!hasRole)
        {
            context.Fail($"{HeaderName} requires the '{requiredRole}' app role.");
            return Task.CompletedTask;
        }

        // Gate 3 -- this exact principal. Unconfigured means closed: without
        // a pinned object id, any application in the tenant holding the role
        // could forward any identity.
        if (string.IsNullOrWhiteSpace(azureAd.ProxyObjectId))
        {
            context.Fail($"{HeaderName} was sent but AzureAd:ProxyObjectId is not configured.");
            return Task.CompletedTask;
        }

        var callerObjectId = caller.FindFirst("oid")?.Value
            ?? caller.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

        if (!string.Equals(callerObjectId, azureAd.ProxyObjectId, StringComparison.OrdinalIgnoreCase))
        {
            context.Fail($"{HeaderName} was sent by an unexpected principal.");
            return Task.CompletedTask;
        }

        // Every gate passed. Now -- and only now -- the forwarded token is
        // parsed, and it is validated in full rather than read.
        var rawUserToken = forwarded.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? forwarded["Bearer ".Length..].Trim()
            : forwarded.Trim();

        try
        {
            var principal = new JwtSecurityTokenHandler()
                .ValidateToken(rawUserToken, CustomJwtParameters(jwt), out _);

            // The rest of the pipeline should see the user, not the proxy:
            // "sub" for the ownership checks, and the "scope" claims the
            // endpoint policies are written against, both come from this
            // token. ScopeClaimsTransformation still runs afterwards and is
            // a no-op here, because a user token already carries "scope".
            context.Principal = principal;
        }
        catch (Exception ex)
        {
            // Deliberately not distinguishing expired from forged in the
            // response. The log carries the detail; the caller gets a 401.
            context.Fail(new SecurityTokenValidationException(
                $"The token in {HeaderName} did not validate.", ex));
        }

        return Task.CompletedTask;
    }
}
