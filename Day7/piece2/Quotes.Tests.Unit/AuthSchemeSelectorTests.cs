using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Authorization;

namespace Quotes.Tests.Unit;

/// <summary>
/// AuthSchemeSelector.Select is the routing decision that used to live
/// inline inside InfrastructureExtensions' AddPolicyScheme(...) lambda (see
/// that class's own comments for why it was pulled out): given the raw
/// Authorization header value, decide "CustomJwt" or "EntraId" by peeking at
/// the token's "aud" claim, without validating anything. Every branch is
/// exercised here directly, with no host and no network call -- the tokens
/// built below are real, well-formed, signed JWTs (so CanReadToken/ReadToken
/// behave exactly as they would on a real request), just signed with a
/// throwaway test-only key that nothing actually trusts. Select() never
/// checks the signature, so that's fine for what it needs to prove.
/// </summary>
public class AuthSchemeSelectorTests
{
    private static string BuildToken(string? audience)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes("unit-test-only-signing-key-nobody-trusts-this-32bytes+"));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "https://issuer.example.com",
            audience: audience,
            claims: Array.Empty<Claim>(),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public void Select_NoAuthorizationHeader_ReturnsCustomJwt()
    {
        AuthSchemeSelector.Select(null).Should().Be(AuthSchemeSelector.CustomJwtScheme);
    }

    [Fact]
    public void Select_EmptyAuthorizationHeader_ReturnsCustomJwt()
    {
        AuthSchemeSelector.Select("").Should().Be(AuthSchemeSelector.CustomJwtScheme);
    }

    [Fact]
    public void Select_MalformedToken_ReturnsCustomJwt()
    {
        // Not JWT-shaped at all (no dot-separated segments) -- exercises
        // the CanReadToken-false path, falling through to CustomJwt.
        AuthSchemeSelector.Select("Bearer this-is-not-a-jwt-at-all")
            .Should().Be(AuthSchemeSelector.CustomJwtScheme);
    }

    [Fact]
    public void Select_CustomJwtShapedAudience_ReturnsCustomJwt()
    {
        // Matches what AuthService.GenerateAccessToken actually issues:
        // aud = "quotes-api", no "api://" prefix.
        var token = BuildToken(audience: "quotes-api");

        AuthSchemeSelector.Select($"Bearer {token}")
            .Should().Be(AuthSchemeSelector.CustomJwtScheme);
    }

    [Fact]
    public void Select_EntraIdShapedAudience_ReturnsEntraId()
    {
        // Matches the shape Entra ID actually issues: "api://<app-id>".
        var token = BuildToken(audience: "api://11111111-2222-3333-4444-555555555555");

        AuthSchemeSelector.Select($"Bearer {token}")
            .Should().Be(AuthSchemeSelector.EntraIdScheme);
    }

    [Fact]
    public void Select_TruncatedToken_ReturnsCustomJwt()
    {
        // A real, valid JWT with its final several characters chopped off.
        // CanReadToken's check is lenient enough to still say "yes" (right
        // segment count, remaining characters still valid base64url
        // charset), but ReadToken then fails to base64-decode/parse the
        // now-incomplete payload and throws -- this is what the try/catch
        // actually exists for: a genuinely corrupted token (a copy-paste
        // truncation, a proxy cutting off a long header), not input
        // CanReadToken already rejects gracefully on its own.
        var validToken = BuildToken(audience: "quotes-api");
        var truncatedToken = validToken[..^10];

        AuthSchemeSelector.Select($"Bearer {truncatedToken}")
            .Should().Be(AuthSchemeSelector.CustomJwtScheme);
    }

    [Fact]
    public void Select_TokenWithNoAudienceClaim_ReturnsCustomJwt()
    {
        // A real, validly-signed JWT that simply never had an audience set
        // (some issuers omit "aud" entirely rather than sending an empty
        // string). parsedToken?.Claims.FirstOrDefault(...) finds nothing,
        // so the null-coalescing fallback to an empty string kicks in, and
        // audience.Contains("api://") is checked against "" -- this is the
        // branch that guards against a NullReferenceException here.
        var token = BuildToken(audience: null);

        AuthSchemeSelector.Select($"Bearer {token}")
            .Should().Be(AuthSchemeSelector.CustomJwtScheme);
    }
}
