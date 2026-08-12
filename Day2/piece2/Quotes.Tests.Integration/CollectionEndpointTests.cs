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
/// Covers /api/collections end to end, including the one test that is the
/// real point of this whole project: proving the FakeClock swapped in by
/// QuotesApiFactory is what the running app actually uses when it stamps
/// a timestamp, not just that DI resolves it.
/// </summary>
public class CollectionEndpointTests : IAsyncLifetime
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

    private async Task<string> CreateAuthenticatedUserAsync()
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

        return authService.GenerateAccessToken(user);
    }

    private static HttpRequestMessage AuthedRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<(int Id, string Location)> CreateCollectionAsync(string token, string name = "My Favorites")
    {
        var request = AuthedRequest(HttpMethod.Post, "/api/collections", token);
        request.Content = JsonContent.Create(new CreateCollectionRequest(name));
        var response = await _client.SendAsync(request);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("id").GetInt32(), response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task PostCollection_WithToken_Returns201WithCallerAsOwner()
    {
        // Arrange
        var token = await CreateAuthenticatedUserAsync();
        var request = AuthedRequest(HttpMethod.Post, "/api/collections", token);
        request.Content = JsonContent.Create(new CreateCollectionRequest("My Favorites"));

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("ownerId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PostCollection_WithoutToken_Returns401()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/api/collections", new CreateCollectionRequest("My Favorites"));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostCollection_WithNameTooShort_ReturnsProblemDetails400()
    {
        // Arrange -- Collection's constructor throws ArgumentException for
        // names under 3 characters; ExceptionHandlingMiddleware maps that
        // to a 400 ProblemDetails response.
        var token = await CreateAuthenticatedUserAsync();
        var request = AuthedRequest(HttpMethod.Post, "/api/collections", token);
        request.Content = JsonContent.Create(new CreateCollectionRequest("ab"));

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task GetCollectionById_WhenMissing_Returns404()
    {
        // Arrange
        var token = await CreateAuthenticatedUserAsync();

        // Act
        var response = await _client.SendAsync(AuthedRequest(HttpMethod.Get, "/api/collections/999999", token));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddItemToCollection_StampsAddedAtFromInjectedFakeClock()
    {
        // Arrange
        var token = await CreateAuthenticatedUserAsync();
        var (id, location) = await CreateCollectionAsync(token);
        var request = AuthedRequest(HttpMethod.Post, $"{location}/items", token);
        request.Content = JsonContent.Create(new AddCollectionItemRequest(QuoteId: 42));

        // Act
        var response = await _client.SendAsync(request);

        // Assert -- this is the test that actually proves the override
        // works: AddedAt comes from IClock.UtcNow inside the endpoint
        // (CollectionEndpointExtensions.cs), so if QuotesApiFactory's
        // FakeClock swap didn't really take effect, this would show the
        // real system time instead and fail.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var addedAt = json.GetProperty("items")[0].GetProperty("addedAt").GetDateTime();
        addedAt.Should().Be(_factory.Clock.UtcNow.UtcDateTime);
    }

    [Fact]
    public async Task AddItemToCollection_WhenAlreadyPresent_ReturnsProblemDetails400()
    {
        // Arrange -- Collection.AddItem throws InvalidOperationException
        // for a duplicate QuoteId, which maps to 400 ProblemDetails.
        var token = await CreateAuthenticatedUserAsync();
        var (_, location) = await CreateCollectionAsync(token);
        var firstAdd = AuthedRequest(HttpMethod.Post, $"{location}/items", token);
        firstAdd.Content = JsonContent.Create(new AddCollectionItemRequest(QuoteId: 7));
        await _client.SendAsync(firstAdd);

        var secondAdd = AuthedRequest(HttpMethod.Post, $"{location}/items", token);
        secondAdd.Content = JsonContent.Create(new AddCollectionItemRequest(QuoteId: 7));

        // Act
        var response = await _client.SendAsync(secondAdd);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task RemoveItemFromCollection_WhenNotInCollection_ReturnsProblemDetails404()
    {
        // Arrange -- Collection.RemoveItem throws KeyNotFoundException
        // when the quote isn't in the collection, which maps to 404
        // ProblemDetails (not a plain empty 404).
        var token = await CreateAuthenticatedUserAsync();
        var (_, location) = await CreateCollectionAsync(token);

        // Act
        var response = await _client.SendAsync(
            AuthedRequest(HttpMethod.Delete, $"{location}/items/999999", token));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }
}
