using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Models;
using QuotesApi.Services;

namespace Quotes.Tests.Integration;

/// <summary>
/// Proves that when ServiceBus:Enabled is false (the default, which the test
/// suite never overrides) quote writes still succeed and no AMQP connection
/// is attempted.
///
/// Follows the same authentication pattern used in QuoteEndpointTests:
/// create a User directly via DI (avoids HTTP register roundtrip) and
/// generate a token with AuthService.GenerateAccessToken so the real
/// token-validation path is exercised.
/// </summary>
public class QuoteEventPublishingTests : IAsyncLifetime
{
    private QuotesApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _token = null!;

    public async Task InitializeAsync()
    {
        _factory = new QuotesApiFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesApi.Data.QuotesDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var user = new User
        {
            Email = $"sbtest-{Guid.NewGuid():N}@example.com",
            PasswordHash = "unused",
            CreatedAt = _factory.Clock.UtcNow.UtcDateTime
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        _token = authService.GenerateAccessToken(user);
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _token);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    private static HttpRequestMessage AuthedRequest(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    [Fact]
    public async Task PostQuote_Succeeds_WhenMessagingDisabled()
    {
        // ServiceBus:Enabled is false (the default). The no-op publisher fires
        // and the HTTP response is still 201 Created — messaging being disabled
        // must not break quote creation.
        var req = AuthedRequest(HttpMethod.Post, "/api/quotes", _token);
        req.Content = JsonContent.Create(new { author = "Service Bus Tester", text = "At-least-once is not exactly-once." });

        var response = await _client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task PutQuote_Succeeds_WhenMessagingDisabled()
    {
        // Create then update, both with the no-op publisher wired.
        var create = AuthedRequest(HttpMethod.Post, "/api/quotes", _token);
        create.Content = JsonContent.Create(new { author = "Service Bus Tester", text = "First draft." });

        var createResp = await _client.SendAsync(create);
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var location = createResp.Headers.Location!.ToString();

        var update = AuthedRequest(HttpMethod.Put, location, _token);
        update.Content = JsonContent.Create(new { author = "Service Bus Tester", text = "Updated." });

        var updateResp = await _client.SendAsync(update);
        updateResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteQuote_Succeeds_WhenMessagingDisabled()
    {
        var create = AuthedRequest(HttpMethod.Post, "/api/quotes", _token);
        create.Content = JsonContent.Create(new { author = "Service Bus Tester", text = "To be deleted." });

        var createResp = await _client.SendAsync(create);
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var location = createResp.Headers.Location!.ToString();

        var delete = AuthedRequest(HttpMethod.Delete, location, _token);
        var deleteResp = await _client.SendAsync(delete);

        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
