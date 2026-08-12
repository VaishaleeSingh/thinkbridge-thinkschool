using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Extensions;
using QuotesApi.Models;
using QuotesApi.Services;

namespace Quotes.Tests.Integration;

/// <summary>
/// Covers /api/quotes end to end: authentication required, scope policies,
/// validation, and the resource-based ownership rule on delete.
/// </summary>
public class QuoteEndpointTests : IAsyncLifetime
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

    // Every logged-in user gets the same six scopes (no roles table --
    // see AuthService.AllScopes), so "an authenticated caller" is fully
    // described by a User row plus a token minted for it. Going through
    // the real IAuthService here (rather than hand-building a JWT) means
    // these tests exercise the exact same token-issuing code path
    // production traffic does, including the FakeClock-driven expiry.
    private async Task<(int UserId, string Token)> CreateAuthenticatedUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesApi.Data.QuotesDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var user = new User
        {
            Email = $"test-{Guid.NewGuid():N}@example.com",
            PasswordHash = "unused-in-these-tests",
            CreatedAt = _factory.Clock.UtcNow.UtcDateTime
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var token = authService.GenerateAccessToken(user);
        return (user.Id, token);
    }

    private static HttpRequestMessage AuthedRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task GetQuotes_WithoutToken_Returns401()
    {
        // Act
        var response = await _client.GetAsync("/api/quotes?page=1&size=10");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetQuotes_WithToken_Returns200Paged()
    {
        // Arrange
        var (_, token) = await CreateAuthenticatedUserAsync();

        // Act
        var response = await _client.SendAsync(AuthedRequest(HttpMethod.Get, "/api/quotes?page=1&size=10", token));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("page").GetInt32().Should().Be(1);
        json.GetProperty("size").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task GetQuotes_WithInvalidPageSize_ReturnsValidationProblem()
    {
        // Arrange
        var (_, token) = await CreateAuthenticatedUserAsync();

        // Act
        var response = await _client.SendAsync(AuthedRequest(HttpMethod.Get, "/api/quotes?page=1&size=0", token));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task PostQuote_WithValidBody_Returns201AndPersists()
    {
        // Arrange
        var (_, token) = await CreateAuthenticatedUserAsync();
        var request = AuthedRequest(HttpMethod.Post, "/api/quotes", token);
        request.Content = JsonContent.Create(new CreateQuoteRequest("Marcus Aurelius", "You have power over your mind."));

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();

        var getResponse = await _client.SendAsync(
            AuthedRequest(HttpMethod.Get, response.Headers.Location!.ToString(), token));
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostQuote_WithMissingAuthor_ReturnsValidationProblem()
    {
        // Arrange
        var (_, token) = await CreateAuthenticatedUserAsync();
        var request = AuthedRequest(HttpMethod.Post, "/api/quotes", token);
        request.Content = JsonContent.Create(new CreateQuoteRequest("", "Some text."));

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task GetQuoteById_WhenExists_Returns200()
    {
        // Arrange
        var (_, token) = await CreateAuthenticatedUserAsync();
        var createRequest = AuthedRequest(HttpMethod.Post, "/api/quotes", token);
        createRequest.Content = JsonContent.Create(new CreateQuoteRequest("Seneca", "Luck favors preparation."));
        var createResponse = await _client.SendAsync(createRequest);

        // Act
        var response = await _client.SendAsync(
            AuthedRequest(HttpMethod.Get, createResponse.Headers.Location!.ToString(), token));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetQuoteById_WhenMissing_Returns404()
    {
        // Arrange
        var (_, token) = await CreateAuthenticatedUserAsync();

        // Act
        var response = await _client.SendAsync(AuthedRequest(HttpMethod.Get, "/api/quotes/999999", token));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteQuote_WhenOwner_Returns204()
    {
        // Arrange
        var (_, token) = await CreateAuthenticatedUserAsync();
        var createRequest = AuthedRequest(HttpMethod.Post, "/api/quotes", token);
        createRequest.Content = JsonContent.Create(new CreateQuoteRequest("Epictetus", "React, don't just happen."));
        var createResponse = await _client.SendAsync(createRequest);

        // Act
        var response = await _client.SendAsync(
            AuthedRequest(HttpMethod.Delete, createResponse.Headers.Location!.ToString(), token));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteQuote_WhenNotOwner_Returns403()
    {
        // Arrange
        var (_, ownerToken) = await CreateAuthenticatedUserAsync();
        var (_, otherToken) = await CreateAuthenticatedUserAsync();
        var createRequest = AuthedRequest(HttpMethod.Post, "/api/quotes", ownerToken);
        createRequest.Content = JsonContent.Create(new CreateQuoteRequest("Aristotle", "We are what we repeatedly do."));
        var createResponse = await _client.SendAsync(createRequest);

        // Act
        var response = await _client.SendAsync(
            AuthedRequest(HttpMethod.Delete, createResponse.Headers.Location!.ToString(), otherToken));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
