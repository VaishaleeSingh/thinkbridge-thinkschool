using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Data;
using QuotesApi.Models;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace QuotesApi.Tests;

/// <summary>
/// Covers the self-issued login/refresh-token system:
///   1. Login_WithValidCredentials_Returns200WithTokens -> the happy path
///   2. Login_WithWrongPassword_Returns401              -> bad credentials are rejected
///   3. Refresh_WithValidToken_RotatesAndReturns200       -> rotation issues a new pair
///   4. Refresh_ReusingAnAlreadyRotatedToken_RevokesEntireFamily_Returns401
///        -> the "revoked refresh chain" scenario: reusing a stale refresh
///           token doesn't just fail on its own, it also kills the NEWER
///           token that replaced it, because at that point a legitimate
///           client and an attacker who stole the old token can no longer
///           be told apart.
///   5. Refresh_WithGarbageToken_Returns401              -> tokens we never issued are rejected
///
/// Each test seeds its own user directly through the DbContext (there's no
/// /api/auth/register endpoint) with a unique, randomly generated email, so
/// tests can run repeatedly against the same on-disk quotes.db without
/// colliding on the unique Email index.
/// </summary>
public class AuthTests : IAsyncLifetime
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

    /// <summary>
    /// Inserts a user straight into the database with a known plaintext
    /// password (hashed the same way AuthService.HashPassword would), and
    /// returns the email/password pair so the test can log in with it over
    /// real HTTP, exactly like a real client would.
    /// </summary>
    private async Task<(string Email, string Password)> SeedUserAsync()
    {
        var email = $"test-{Guid.NewGuid():N}@example.com";
        const string password = "Correct-Horse-Battery-Staple-1";

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();

        var hasher = new PasswordHasher<User>();
        var user = new User
        {
            Email = email,
            CreatedAt = DateTime.UtcNow
        };
        user.PasswordHash = hasher.HashPassword(user, password);

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return (email, password);
    }

    private sealed record TokenPair(string AccessToken, string RefreshToken, int ExpiresIn, string TokenType);

    private static async Task<TokenPair> ReadTokenPairAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        return new TokenPair(
            json.GetProperty("accessToken").GetString()!,
            json.GetProperty("refreshToken").GetString()!,
            json.GetProperty("expiresIn").GetInt32(),
            json.GetProperty("tokenType").GetString()!);
    }

    [Fact]
    public async Task Login_WithValidCredentials_Returns200WithTokens()
    {
        var (email, password) = await SeedUserAsync();

        var response = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var tokens = await ReadTokenPairAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
        Assert.Equal(900, tokens.ExpiresIn);
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        var (email, _) = await SeedUserAsync();

        var response = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email, password = "definitely-not-the-right-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithValidToken_RotatesAndReturns200()
    {
        var (email, password) = await SeedUserAsync();
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });
        var original = await ReadTokenPairAsync(loginResponse);

        var refreshResponse = await _client.PostAsJsonAsync(
            "/api/auth/refresh",
            new { refreshToken = original.RefreshToken });

        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);

        var rotated = await ReadTokenPairAsync(refreshResponse);

        // The refresh token is 32 random bytes, so it always changes on
        // rotation regardless of timing.
        Assert.NotEqual(original.RefreshToken, rotated.RefreshToken);

        // The access token is NOT guaranteed to differ byte-for-byte: JWT
        // "exp"/"nbf" claims only have one-second resolution, so a login
        // immediately followed by a refresh (as in this test) can
        // legitimately mint two tokens with identical claims and
        // therefore an identical signature. What matters here is that a
        // new, valid access token came back -- not that its string
        // representation changed.
        Assert.False(string.IsNullOrWhiteSpace(rotated.AccessToken));
    }

    [Fact]
    public async Task Refresh_ReusingAnAlreadyRotatedToken_RevokesEntireFamily_Returns401()
    {
        var (email, password) = await SeedUserAsync();
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });
        var original = await ReadTokenPairAsync(loginResponse);

        // First rotation: legitimate use. original.RefreshToken is now
        // marked ReplacedByToken and can never be exchanged again.
        var firstRefresh = await _client.PostAsJsonAsync(
            "/api/auth/refresh",
            new { refreshToken = original.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, firstRefresh.StatusCode);
        var rotated = await ReadTokenPairAsync(firstRefresh);

        // Second attempt to use the SAME original token -- this is what an
        // attacker replaying a stolen (but already-used) refresh token
        // looks like. ValidateTokenAsync sees ReplacedByToken is already
        // set and treats this as reuse, revoking the whole token family.
        var reuseAttempt = await _client.PostAsJsonAsync(
            "/api/auth/refresh",
            new { refreshToken = original.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, reuseAttempt.StatusCode);

        // The token that legitimately replaced it (`rotated`) is now ALSO
        // dead, even though it was never itself reused -- the entire
        // family was revoked, not just the stolen token. This is the whole
        // point of family-based reuse detection: once reuse is seen,
        // nothing further down that chain can be trusted anymore.
        var tryUseRotatedToken = await _client.PostAsJsonAsync(
            "/api/auth/refresh",
            new { refreshToken = rotated.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, tryUseRotatedToken.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithGarbageToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/refresh",
            new { refreshToken = "this-was-never-issued-by-anyone" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
