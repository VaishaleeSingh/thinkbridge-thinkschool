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
/// SQL Server has two very different date/time column types: the legacy
/// "datetime" (rounds to the nearest ~3.33ms) and "datetime2" (100ns
/// precision, matching .NET's DateTime exactly). EF Core's SQL Server
/// provider defaults to datetime2 today, but that is a default, not a
/// guarantee -- if a future migration or an explicit
/// .HasColumnType("datetime") ever regresses this, timestamps would start
/// silently losing precision on save. This test pins the current,
/// correct behavior so a regression fails loudly here instead of showing
/// up as "why is this timestamp off by a couple milliseconds" later.
/// </summary>
[Collection("SqlServer")]
public class SqlServerDatetimePrecisionTests : IAsyncLifetime
{
    private readonly MsSqlContainerFixture _containerFixture;
    private SqlServerQuotesApiFactory _factory = null!;
    private HttpClient _client = null!;

    public SqlServerDatetimePrecisionTests(MsSqlContainerFixture containerFixture)
    {
        _containerFixture = containerFixture;
    }

    public async Task InitializeAsync()
    {
        if (!_containerFixture.IsStarted) return;
        _factory = new SqlServerQuotesApiFactory(_containerFixture.ConnectionString);
        _client = _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory != null) await _factory.DisposeAsync();
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

    [Fact]
    public async Task AddItemToCollection_AddedAtRoundTripsExactlyThroughSqlServersDatetime2Column()
    {
        if (!_containerFixture.IsStarted) return;
        var token = await CreateAuthenticatedUserAsync();

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/collections");
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        createRequest.Content = JsonContent.Create(new CreateCollectionRequest("Precision Check"));
        var createResponse = await _client.SendAsync(createRequest);
        var location = createResponse.Headers.Location!.ToString();

        var addRequest = new HttpRequestMessage(HttpMethod.Post, $"{location}/items");
        addRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        addRequest.Content = JsonContent.Create(new AddCollectionItemRequest(QuoteId: 1));

        var response = await _client.SendAsync(addRequest);

        response.IsSuccessStatusCode.Should().BeTrue();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var addedAt = body.GetProperty("items")[0].GetProperty("addedAt").GetDateTime();

        // Exact equality, not "within a second" -- that is the whole
        // point: datetime2 should not truncate or round anything at all.
        addedAt.Should().Be(_factory.Clock.UtcNow.UtcDateTime);
    }
}
