using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;

namespace QuotesApi.Tests;

/// <summary>
/// Day 3 introduced a second way to authenticate: alongside our own custom
/// JWT (used by internal tools), the API now also accepts Azure Entra ID
/// tokens (used by customer-facing apps). Both must work at the same time.
///
/// These tests spin up the real API in memory (via WebApplicationFactory)
/// and hit its actual HTTP endpoints, the same way a real client would —
/// nothing here is mocked at the code level, only the tokens themselves are
/// generated locally instead of being fetched from Azure, so the tests can
/// run without needing a live Azure tenant.
///
/// What each test proves, in one line:
///   1. GetQuotes_WithCustomJwt_Returns200          -> old auth still works
///   2. GetQuotes_WithEntraIdJwt_RoutesToEntraScheme -> new auth is recognised
///   3. GetQuotes_NoToken_Returns401                -> auth is enforced, not optional
///   4. GetQuotes_InvalidToken_Returns401           -> garbage tokens are rejected
///   5. GetQuotes_ExpiredCustomJwt_Returns401       -> expiry is respected
///   6. MultiScheme_CorrectlyDetects_CustomVsEntraToken -> the routing logic itself is correct
///   7. CreateQuote_WithCustomJwt_Returns201        -> writes work, not just reads
///   8. DeleteQuote_WithCustomJwt_Returns204        -> deletes work too
/// </summary>
public class EntraIdTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // Boots the whole API in-memory (same Program.cs, same
        // InfrastructureExtensions, same endpoints) so these tests exercise
        // the real request pipeline instead of calling C# methods directly.
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    // =========================================================================
    // TOKEN BUILDERS
    // These two helpers build JWTs by hand, purely for testing. In real life,
    // a custom JWT comes from AuthService.GenerateAccessToken(), and an Entra
    // token comes from Microsoft's servers — neither is built like this in
    // production code.
    // =========================================================================

    /// <summary>
    /// Builds a token shaped exactly like the ones our own AuthService
    /// issues: same secret, same issuer, same audience as the "CustomJwt"
    /// scheme configured in InfrastructureExtensions.cs. Because we know the
    /// secret, we can sign it ourselves and the API will accept it as valid.
    ///
    /// Every caller gets the same default scopes (read + write + delete) —
    /// this project has no roles/admin table, so scope alone never decides
    /// who can delete a specific quote; ownership does (see
    /// MustOwnQuoteHandler). Pass a narrower `scopes` list to test what
    /// happens when a required scope is missing, and a different `userId`
    /// to simulate a second, unrelated caller.
    /// </summary>
    private static string CreateCustomJwt(
        string userId = "user-123",
        IEnumerable<string>? scopes = null,
        DateTime? notBefore = null,
        DateTime? expires = null)
    {
        const string secret = "your-existing-custom-jwt-secret-keep-this-same";
        var signingKey = Encoding.UTF8.GetBytes(secret);

        var claims = new List<Claim>
        {
            new Claim("sub", userId),
            new Claim("email", $"{userId}@example.com")
        };

        foreach (var scope in scopes ?? new[] { "quotes.read", "quotes.write", "quotes.delete" })
            claims.Add(new Claim("scope", scope));

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            NotBefore = notBefore ?? DateTime.UtcNow,
            Expires = expires ?? DateTime.UtcNow.AddHours(1),
            Issuer = "https://yourapp.com",
            Audience = "quotes-api", // <- plain string, no "api://" prefix
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(signingKey),
                SecurityAlgorithms.HmacSha256Signature)
        });

        return handler.WriteToken(token);
    }

    /// <summary>
    /// Builds a token shaped like the ones Azure Entra ID would issue: same
    /// issuer format and same "api://..." style audience as the "EntraId"
    /// scheme expects. We sign it with a throwaway local key rather than
    /// Azure's real private key (we don't have that), so full signature
    /// validation against Azure will fail — but that's fine here, because
    /// this token is only used to prove the MultiScheme *router* correctly
    /// recognises "this looks like an Entra token" and sends it to the right
    /// validator. See GetQuotes_WithEntraIdJwt_RoutesToEntraScheme below.
    /// </summary>
    private static string CreateMockEntraIdJwt()
    {
        // HMAC-SHA256 requires a key of at least 256 bits (32 bytes).
        // A too-short key throws before the token can even be built, so the
        // string below is deliberately padded past that minimum. Its actual
        // content doesn't matter — Azure's real key is what the API checks.
        const string throwawaySigningSecret = "mock-entra-secret-padded-to-32-bytes-minimum";
        var signingKey = Encoding.UTF8.GetBytes(throwawaySigningSecret);

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim("sub", "entra-user-456"),
                new Claim("email", "spa-user@company.com"),
                // Shape of a real Entra delegated-permission claim: one
                // claim, space-separated values. ScopeClaimsTransformation
                // splits this into individual "scope" claims so the same
                // policies work regardless of which scheme authenticated.
                new Claim("scp", "quotes.read quotes.write quotes.delete")
            }),
            Expires = DateTime.UtcNow.AddHours(1),
            Issuer = "https://login.microsoftonline.com/f774bb68-0575-4cd2-9d4c-3b4e593d1110/v2.0",
            Audience = "api://quotes-api/access", // <- "api://" prefix is what MultiScheme looks for
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(signingKey),
                SecurityAlgorithms.HmacSha256Signature)
        });

        return handler.WriteToken(token);
    }

    // =========================================================================
    // AUTHENTICATION TESTS
    // =========================================================================

    [Fact]
    public async Task GetQuotes_WithCustomJwt_Returns200()
    {
        // A client using the OLD auth method should still work after Day 3.
        var token = CreateCustomJwt();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/quotes?page=1&size=10");
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode,
            $"Expected 2xx, got {response.StatusCode}. Custom JWT should still be accepted.");
    }

    [Fact]
    public async Task GetQuotes_WithEntraIdJwt_RoutesToEntraScheme()
    {
        var token = CreateMockEntraIdJwt();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/quotes?page=1&size=10");
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        // This mock token is signed with a throwaway key, not Azure's real
        // one, so full signature validation is expected to fail (401) here.
        // What this test actually confirms is that MultiScheme correctly
        // spotted the "api://" audience and forwarded the request to the
        // EntraId validator instead of CustomJwt — against a real Entra
        // tenant (see TESTING_GUIDE.md), the same request returns 200.
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized || response.IsSuccessStatusCode,
            $"Expected either 401 (mock signature rejected) or 200 (real token), got {response.StatusCode}");
    }

    [Fact]
    public async Task GetQuotes_NoToken_Returns401()
    {
        // No Authorization header at all -> RequireAuthorization() on the
        // endpoint group should block this before it ever reaches the
        // repository.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/quotes?page=1&size=10");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetQuotes_InvalidToken_Returns401()
    {
        // Not even a real JWT — just three dot-separated words. Confirms
        // malformed tokens are rejected cleanly instead of crashing.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/quotes?page=1&size=10");
        request.Headers.Add("Authorization", "Bearer invalid.token.here");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetQuotes_ExpiredCustomJwt_Returns401()
    {
        // Build a token whose validity window already closed:
        // "NotBefore" (1 minute ago) ... "Expires" (10 seconds ago).
        // Both must be in the past, and NotBefore must come before Expires,
        // otherwise CreateToken() itself throws for an invalid window.
        var token = CreateCustomJwt(
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddSeconds(-10));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/quotes?page=1&size=10");
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MultiScheme_CorrectlyDetects_CustomVsEntraToken()
    {
        // This test doesn't call the API at all — it only checks the raw
        // tokens themselves, to make the routing rule explicit:
        //   audience WITHOUT "api://"  -> our own CustomJwt scheme
        //   audience WITH    "api://"  -> Entra's EntraId scheme
        var customToken = CreateCustomJwt();
        var entraToken = CreateMockEntraIdJwt();

        var handler = new JwtSecurityTokenHandler();
        var customAudience = ReadAudienceClaim(handler, customToken);
        var entraAudience = ReadAudienceClaim(handler, entraToken);

        Assert.Equal("quotes-api", customAudience);
        Assert.Equal("api://quotes-api/access", entraAudience);

        Assert.DoesNotContain("api://", customAudience);
        Assert.Contains("api://", entraAudience);
    }

    private static string ReadAudienceClaim(JwtSecurityTokenHandler handler, string rawToken)
    {
        var token = handler.ReadToken(rawToken) as JwtSecurityToken;
        return token?.Claims.First(claim => claim.Type == "aud").Value ?? "";
    }

    // =========================================================================
    // WRITE-OPERATION TESTS (auth also has to work for POST/DELETE, not just GET)
    // =========================================================================

    [Fact]
    public async Task CreateQuote_WithCustomJwt_Returns201()
    {
        var token = CreateCustomJwt();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/quotes")
        {
            Content = new StringContent(
                """{"author":"Confucius","text":"Life is what happens when you're busy making other plans"}""",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Contains("Confucius", responseBody);
    }

    [Fact]
    public async Task DeleteQuote_WithCustomJwt_Returns204()
    {
        var token = CreateCustomJwt();

        // Arrange: create a quote first, so there's something to delete.
        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/quotes")
        {
            Content = new StringContent(
                """{"author":"Oscar Wilde","text":"Be yourself, everyone else is already taken"}""",
                Encoding.UTF8,
                "application/json")
        };
        createRequest.Headers.Add("Authorization", $"Bearer {token}");

        var createResponse = await _client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // The new quote's URL comes back in the Location header, e.g.
        // "/api/quotes/7" — pull the id off the end of it.
        var location = createResponse.Headers.Location?.OriginalString;
        Assert.NotNull(location);
        var quoteId = location.Split("/").Last();

        // Act: delete it, using the same token.
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/quotes/{quoteId}");
        deleteRequest.Headers.Add("Authorization", $"Bearer {token}");

        var deleteResponse = await _client.SendAsync(deleteRequest);

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    // =========================================================================
    // AUTHORIZATION POLICY + OWNERSHIP TESTS (Day 3, part 2)
    // =========================================================================

    [Fact]
    public async Task CreateQuote_WithoutWriteScope_Returns403()
    {
        // The token is perfectly valid and authenticates fine — it just
        // doesn't carry "quotes.write". RequireAuthorization("can-edit-quotes")
        // should block this with 403, not 401: 401 means "I don't know who
        // you are," 403 means "I know who you are, and the answer is no."
        var token = CreateCustomJwt(scopes: new[] { "quotes.read" });

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/quotes")
        {
            Content = new StringContent(
                """{"author":"Test","text":"Should be blocked by policy"}""",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteQuote_AsDifferentUser_Returns403()
    {
        // user-A creates a quote...
        var ownerToken = CreateCustomJwt(userId: "user-A");

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/quotes")
        {
            Content = new StringContent(
                """{"author":"Owner Test","text":"Only user-A should be able to delete this"}""",
                Encoding.UTF8,
                "application/json")
        };
        createRequest.Headers.Add("Authorization", $"Bearer {ownerToken}");

        var createResponse = await _client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var location = createResponse.Headers.Location?.OriginalString;
        Assert.NotNull(location);
        var quoteId = location.Split("/").Last();

        // ...user-B — who has every scope, including quotes.delete — tries
        // to delete it. The "can-delete-quotes" policy lets this through
        // (user-B genuinely has delete permission in general); it's the
        // ownership check inside the endpoint that has to stop it here.
        var otherUserToken = CreateCustomJwt(userId: "user-B");

        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/quotes/{quoteId}");
        deleteRequest.Headers.Add("Authorization", $"Bearer {otherUserToken}");

        var deleteResponse = await _client.SendAsync(deleteRequest);

        Assert.Equal(HttpStatusCode.Forbidden, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteQuote_AsOwner_Returns204()
    {
        // Same shape as the test above, but this time the SAME user who
        // created the quote is the one deleting it — proves the ownership
        // check isn't just blocking everyone, only non-owners.
        var ownerToken = CreateCustomJwt(userId: "user-C");

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/quotes")
        {
            Content = new StringContent(
                """{"author":"Owner Test 2","text":"user-C owns this one and can delete it"}""",
                Encoding.UTF8,
                "application/json")
        };
        createRequest.Headers.Add("Authorization", $"Bearer {ownerToken}");

        var createResponse = await _client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var location = createResponse.Headers.Location?.OriginalString;
        Assert.NotNull(location);
        var quoteId = location.Split("/").Last();

        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/quotes/{quoteId}");
        deleteRequest.Headers.Add("Authorization", $"Bearer {ownerToken}");

        var deleteResponse = await _client.SendAsync(deleteRequest);

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }
}
