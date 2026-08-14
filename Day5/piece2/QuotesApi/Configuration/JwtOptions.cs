using System.ComponentModel.DataAnnotations;

namespace QuotesApi.Configuration;

/// <summary>
/// Everything about the tokens this API issues and accepts, bound from the
/// "Jwt" configuration section.
///
/// WHY THIS TYPE EXISTS AT ALL:
/// these values used to be string literals repeated in four places that all
/// had to agree -- the issuer and audience were written once in
/// AuthService.GenerateAccessToken when minting a token and again in
/// InfrastructureExtensions as ValidIssuer/ValidAudience when validating
/// one, while the lifetime was a const in AuthService and a hand-copied
/// "900" in AuthEndpointExtensions telling clients when to refresh. Nothing
/// enforced that they matched; InfrastructureExtensions carried a comment
/// asking a human to remember. Change one and either every token is
/// rejected by the API that issued it, or clients are told the wrong expiry
/// and refresh at the wrong moment -- with nothing failing loudly. Binding
/// once and reading the same instance everywhere makes that drift
/// impossible rather than merely discouraged.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// The HMAC signing key. Never present in appsettings.json -- it comes
    /// from user-secrets locally and from Key Vault (through configuration)
    /// in deployed environments.
    ///
    /// The minimum length is not arbitrary: HMAC-SHA256 requires a key of
    /// at least 256 bits, and a shorter one throws deep inside the token
    /// handler at the moment someone tries to log in. Validating it here
    /// turns that into a startup failure naming the property.
    /// </summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Jwt:Secret is required. Set it with 'dotnet user-secrets set \"Jwt:Secret\" ...' locally, or from Key Vault in deployed environments. It must never be committed to appsettings.json.")]
    [MinLength(32, ErrorMessage = "Jwt:Secret must be at least 32 characters -- HMAC-SHA256 needs a 256-bit key.")]
    public string Secret { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string Issuer { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string Audience { get; init; } = string.Empty;

    /// <summary>
    /// Kept short on purpose: a stolen access token stops working quickly on
    /// its own. Long-lived sessions are handled by refresh tokens instead,
    /// which can be individually revoked and are rotated on every use.
    ///
    /// Bound from a duration string such as "00:15:00".
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:30", "24:00:00",
        ErrorMessage = "Jwt:AccessTokenLifetime must be between 30 seconds and 24 hours.")]
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);
}
