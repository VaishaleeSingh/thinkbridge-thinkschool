using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Data;
using QuotesApi.Extensions;
using QuotesApi.Models;
using QuotesApi.Services;

namespace Quotes.Tests.Integration;

/// <summary>
/// Two properties that don't belong to any single endpoint: that the real
/// EF Core migrations actually run against the in-memory database on
/// startup, and that two separate test factories never see each other's
/// data. Both tests manage their own QuotesApiFactory instances directly
/// (rather than through IAsyncLifetime) because the point of each test IS
/// the factory lifecycle itself.
/// </summary>
public class CrossCuttingTests
{
    [Fact]
    public async Task Factory_OnStartup_AppliesMigrationsToFreshInMemoryDatabase()
    {
        // Arrange
        await using var factory = new QuotesApiFactory();
        _ = factory.CreateClient(); // forces the host (and its startup migration call) to actually run

        // Act
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
        var applied = await db.Database.GetAppliedMigrationsAsync();
        var defined = db.Database.GetMigrations();

        // Assert -- every migration file in the project actually applied
        // to this fresh in-memory database, not just "some" of them.
        applied.Should().NotBeEmpty();
        applied.Should().BeEquivalentTo(defined);
    }

    [Fact]
    public async Task TwoSequentialFactories_DoNotShareState()
    {
        // Arrange -- first factory creates data and is fully disposed
        // before the second one is even constructed.
        string tokenA;
        await using (var factoryA = new QuotesApiFactory())
        {
            var clientA = factoryA.CreateClient();
            tokenA = await CreateAuthenticatedUserAsync(factoryA);

            var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/quotes");
            createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
            createRequest.Content = JsonContent.Create(new CreateQuoteRequest("Marcus Aurelius", "Confine yourself to the present."));
            await clientA.SendAsync(createRequest);
        }

        // Act -- a brand new factory, brand new in-memory SQLite
        // connection, brand new user/token.
        await using var factoryB = new QuotesApiFactory();
        var clientB = factoryB.CreateClient();
        var tokenB = await CreateAuthenticatedUserAsync(factoryB);

        var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/quotes?page=1&size=50");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);
        var listResponse = await clientB.SendAsync(listRequest);
        var json = await listResponse.Content.ReadFromJsonAsync<JsonElement>();

        // Assert -- if the two factories shared a database, this would be
        // 1 (or more); a fresh, isolated database means 0.
        json.GetProperty("total").GetInt32().Should().Be(0);
    }

    private static async Task<string> CreateAuthenticatedUserAsync(QuotesApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var user = new User
        {
            Email = $"test-{Guid.NewGuid():N}@example.com",
            PasswordHash = "unused-in-these-tests",
            CreatedAt = factory.Clock.UtcNow.UtcDateTime
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return authService.GenerateAccessToken(user);
    }
}
