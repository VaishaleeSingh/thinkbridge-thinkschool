using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;

namespace QuotesApi.Tests;

/// <summary>
/// /api/collections used to have NO authorization at all -- any anonymous
/// request could create or read a collection. These tests prove the fix,
/// the same way EntraIdTests proves it for /api/quotes:
///   1. CreateCollection_NoToken_Returns401          -> auth is enforced at all
///   2. CreateCollection_WithoutWriteScope_Returns403 -> right identity, wrong permission
///   3. CreateCollection_WithWriteScope_Returns201    -> the happy path still works
///   4. GetCollection_WithoutReadScope_Returns403     -> read and write scopes are independent
/// </summary>
public class CollectionAuthorizationTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    private static string CreateToken(string userId, params string[] scopes)
    {
        const string secret = "your-existing-custom-jwt-secret-keep-this-same";
        var signingKey = Encoding.UTF8.GetBytes(secret);

        var claims = new List<Claim>
        {
            new Claim("sub", userId),
            new Claim("email", $"{userId}@example.com")
        };

        foreach (var scope in scopes)
            claims.Add(new Claim("scope", scope));

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            NotBefore = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddHours(1),
            Issuer = "https://yourapp.com",
            Audience = "quotes-api",
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(signingKey),
                SecurityAlgorithms.HmacSha256Signature)
        });

        return handler.WriteToken(token);
    }

    [Fact]
    public async Task CreateCollection_NoToken_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/collections")
        {
            Content = new StringContent(
                """{"name": "Anonymous attempt"}""",
                Encoding.UTF8,
                "application/json")
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateCollection_WithoutWriteScope_Returns403()
    {
        // Authenticated, but only holds collections.read -- creating a
        // collection needs collections.write.
        var token = CreateToken("read-only-user", "collections.read");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/collections")
        {
            Content = new StringContent(
                """{"name": "Should be blocked by policy"}""",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateCollection_WithWriteScope_Returns201()
    {
        var token = CreateToken("collection-owner", "collections.write");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/collections")
        {
            Content = new StringContent(
                """{"name": "A real collection"}""",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        // Confirms OwnerId was stamped from the token's "sub" claim, not
        // taken from the (now-removed) request-body field.
        Assert.Contains("collection-owner", body);
    }

    [Fact]
    public async Task GetCollection_WithoutReadScope_Returns403()
    {
        // Create it with a fully-scoped token first...
        var creatorToken = CreateToken("creator", "collections.write");

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/collections")
        {
            Content = new StringContent(
                """{"name": "Needs read scope to view"}""",
                Encoding.UTF8,
                "application/json")
        };
        createRequest.Headers.Add("Authorization", $"Bearer {creatorToken}");

        var createResponse = await _client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var location = createResponse.Headers.Location?.OriginalString;
        Assert.NotNull(location);
        var collectionId = location.Split("/").Last();

        // ...then try to read it with a token that has every OTHER scope
        // except collections.read.
        var readerToken = CreateToken("reader", "collections.write", "collections.delete");

        var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/collections/{collectionId}");
        getRequest.Headers.Add("Authorization", $"Bearer {readerToken}");

        var getResponse = await _client.SendAsync(getRequest);

        Assert.Equal(HttpStatusCode.Forbidden, getResponse.StatusCode);
    }
}
