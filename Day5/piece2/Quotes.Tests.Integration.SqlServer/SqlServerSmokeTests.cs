using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Data;
using QuotesApi.Extensions;
using QuotesApi.Models;

namespace Quotes.Tests.Integration.SqlServer;

/// <summary>
/// One straightforward end-to-end pass (login, then use the token to
/// create a collection) against the real containerized SQL Server -- a
/// parity check that basic routing, DI, and the migrated schema all wire
/// up correctly against the real engine, independent of the more
/// targeted quirk tests elsewhere in this project.
/// </summary>
[Collection("SqlServer")]
public class SqlServerSmokeTests : IAsyncLifetime
{
    private const string Password = "Correct-Horse-Battery-Staple-1";

    private readonly MsSqlContainerFixture _containerFixture;
    private SqlServerQuotesApiFactory _factory = null!;
    private HttpClient _client = null!;

    public SqlServerSmokeTests(MsSqlContainerFixture containerFixture)
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

    [Fact]
    public async Task LoginThenCreateCollection_EndToEndAgainstRealSqlServer_Succeeds()
    {
        if (!_containerFixture.IsStarted) return;
        var email = $"test-{Guid.NewGuid():N}@example.com";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
            var hasher = new PasswordHasher<User>();
            db.Users.Add(new User
            {
                Email = email,
                PasswordHash = hasher.HashPassword(new User(), Password),
                CreatedAt = _factory.Clock.UtcNow.UtcDateTime
            });
            await db.SaveChangesAsync();
        }

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/collections");
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        createRequest.Content = JsonContent.Create(new CreateCollectionRequest("Smoke Test Collection"));

        var createResponse = await _client.SendAsync(createRequest);

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
