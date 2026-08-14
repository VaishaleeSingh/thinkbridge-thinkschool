using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Data;
using QuotesApi.Extensions;
using QuotesApi.Models;
using QuotesApi.Services;

namespace Quotes.Tests.Integration.SqlServer;

/// <summary>
/// This concern is specific to a real, multi-connection engine, not to
/// SQLite: the SQLite integration project's ":memory:" database is one
/// SqliteConnection kept open for the whole factory lifetime, which
/// effectively serializes every operation through that single connection.
/// True concurrent access, with real row/page locking and
/// timing-dependent races, only exists against an engine that actually
/// supports multiple simultaneous connections -- this containerized SQL
/// Server.
///
/// CollectionItem's primary key is the composite (CollectionId, QuoteId)
/// -- see QuotesDbContext.OnModelCreating -- so two requests racing to
/// add the same QuoteId to the same collection are racing to insert the
/// same primary key. The assertion below deliberately checks the final
/// database state rather than which HTTP response "won": which request
/// wins a genuine race is inherently nondeterministic, but the invariant
/// that actually matters -- exactly one item ends up added, never zero
/// and never two -- is not.
/// </summary>
[Collection("SqlServer")]
public class SqlServerConcurrencyTests : IAsyncLifetime
{
    private readonly MsSqlContainerFixture _containerFixture;
    private SqlServerQuotesApiFactory _factory = null!;
    private HttpClient _client = null!;

    public SqlServerConcurrencyTests(MsSqlContainerFixture containerFixture)
    {
        _containerFixture = containerFixture;
    }

    public async Task InitializeAsync()
    {
        _factory = new SqlServerQuotesApiFactory(_containerFixture.ConnectionString);
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
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
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

    [Fact]
    public async Task TwoConcurrentRequests_AddingSameQuoteId_ResultInExactlyOneItem()
    {
        var token = await CreateAuthenticatedUserAsync();

        var createRequest = AuthedRequest(HttpMethod.Post, "/api/collections", token);
        createRequest.Content = JsonContent.Create(new CreateCollectionRequest("Race Condition Check"));
        var createResponse = await _client.SendAsync(createRequest);
        var location = createResponse.Headers.Location!.ToString();

        HttpRequestMessage AddItemRequest() => new HttpRequestMessage(HttpMethod.Post, $"{location}/items")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = JsonContent.Create(new AddCollectionItemRequest(QuoteId: 99))
        };

        // Fire both requests together rather than one after the other --
        // awaiting sequentially would only prove the duplicate-item check
        // works (already covered in Quotes.Tests.Integration), not that
        // the database's own constraint holds up when two requests
        // genuinely overlap in time.
        var first = _client.SendAsync(AddItemRequest());
        var second = _client.SendAsync(AddItemRequest());
        var responses = await Task.WhenAll(first, second);

        responses.Count(r => r.IsSuccessStatusCode).Should().BeLessThanOrEqualTo(
            1, "at most one of the two racing requests should have succeeded");

        var getResponse = await _client.SendAsync(AuthedRequest(HttpMethod.Get, location, token));
        var finalState = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var matchingItems = finalState.GetProperty("items").EnumerateArray()
            .Count(i => i.GetProperty("quoteId").GetInt32() == 99);

        matchingItems.Should().Be(1, "the composite primary key on (CollectionId, QuoteId) should let exactly one insert win, not zero and not two");
    }
}
