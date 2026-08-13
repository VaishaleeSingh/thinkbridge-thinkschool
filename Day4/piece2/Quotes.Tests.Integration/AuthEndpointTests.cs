using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Data;
using QuotesApi.Extensions;
using QuotesApi.Models;

namespace Quotes.Tests.Integration;

/// <summary>
/// Covers /api/auth/login, /refresh, and /logout end to end: real HTTP
/// calls into the real app, backed by a fresh in-memory database (see
/// QuotesApiFactory).
/// </summary>
public class AuthEndpointTests : IAsyncLifetime
{
    private QuotesApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new QuotesApiFactory();
        _client = _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    private const string Password = "Correct-Horse-Battery-Staple-1";

    private async Task<string> SeedUserAsync(string? email = null)
    {
        email ??= $"test-{Guid.NewGuid():N}@example.com";

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();

        var hasher = new PasswordHasher<User>();
        var user = new User
        {
            Email = email,
            PasswordHash = hasher.HashPassword(new User(), Password),
            CreatedAt = _factory.Clock.UtcNow.UtcDateTime
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return email;
    }

    [Fact]
    public async Task Login_WithValidCredentials_Returns200WithTokens()
    {
        // Arrange
        var email = await SeedUserAsync();

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        body.Should().NotBeNull();
        body!.AccessToken.Should().NotBeNullOrWhiteSpace();
        body.RefreshToken.Should().NotBeNullOrWhiteSpace();
        body.TokenType.Should().Be("Bearer");
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        // Arrange
        var email = await SeedUserAsync();

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "wrong-password"));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_WithMissingFields_ReturnsValidationProblem()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest("", ""));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Refresh_WithValidToken_Returns200WithNewPair()
    {
        // Arrange
        var email = await SeedUserAsync();
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        var original = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();

        // Act
        var refreshResponse = await _client.PostAsJsonAsync(
            "/api/auth/refresh",
            new RefreshRequest(original!.RefreshToken));

        // Assert
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var rotated = await refreshResponse.Content.ReadFromJsonAsync<LoginResponse>();
        rotated!.RefreshToken.Should().NotBe(original.RefreshToken);
    }

    [Fact]
    public async Task Refresh_WithMissingToken_ReturnsValidationProblem()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(""));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Logout_RevokesToken_SubsequentRefreshReturns401()
    {
        // Arrange
        var email = await SeedUserAsync();
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        var tokens = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();

        // Act
        var logoutResponse = await _client.PostAsJsonAsync(
            "/api/auth/logout",
            new LogoutRequest(tokens!.RefreshToken));
        var refreshAfterLogout = await _client.PostAsJsonAsync(
            "/api/auth/refresh",
            new RefreshRequest(tokens.RefreshToken));

        // Assert
        logoutResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        refreshAfterLogout.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
