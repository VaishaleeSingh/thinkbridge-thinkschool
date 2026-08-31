using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuotesApi.BackgroundJobs;
using QuotesApi.Extensions;
using QuotesApi.Models;
using QuotesApi.Services;

namespace Quotes.Tests.Integration;

public class BackgroundJobEndpointTests : IAsyncLifetime
{
    private readonly ControlledProcessor _processor = new();
    private QuotesApiFactory _factory = null!;
    private WebApplicationFactory<Program> _application = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new QuotesApiFactory();
        _application = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("BackgroundJobs:QueueCapacity", "1");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IQuoteAuthorReportProcessor>();
                services.AddScoped<IQuoteAuthorReportProcessor>(_ => _processor);
            });
        });

        _client = _application.CreateClient();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _application.DisposeAsync();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task PostReport_WithoutToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/background-jobs/quote-author-reports",
            new CreateQuoteAuthorReportRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostReport_Returns202BeforeProcessorCompletes_ThenExposesResult()
    {
        var (_, token) = await CreateAuthenticatedUserAsync();
        var request = AuthedRequest(
            HttpMethod.Post,
            "/api/background-jobs/quote-author-reports",
            token);
        request.Content = JsonContent.Create(new CreateQuoteAuthorReportRequest(5));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        _processor.Release.Task.IsCompleted.Should().BeFalse(
            "the HTTP request must not wait for the slow processor");

        await _processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var running = await GetStatusAsync(response.Headers.Location!, token);
        running.GetProperty("status").GetString().Should().Be("Running");

        _processor.Release.TrySetResult();

        var completed = await WaitForStatusAsync(
            response.Headers.Location!,
            token,
            "Succeeded");
        completed.GetProperty("result")
            .GetProperty("totalQuotes")
            .GetInt32()
            .Should().Be(3);
    }

    [Fact]
    public async Task PostReport_WhenQueueIsFull_Returns503WithRetryAfter()
    {
        var (_, token) = await CreateAuthenticatedUserAsync();

        var first = await PostReportAsync(token);
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await _processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var queued = await PostReportAsync(token);
        queued.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var rejected = await PostReportAsync(token);

        rejected.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        rejected.Headers.RetryAfter?.Delta.Should().Be(TimeSpan.FromSeconds(5));
        _processor.Release.TrySetResult();
    }

    [Fact]
    public async Task GetStatus_WhenCalledByAnotherUser_Returns404()
    {
        var (_, ownerToken) = await CreateAuthenticatedUserAsync();
        var (_, otherToken) = await CreateAuthenticatedUserAsync();
        var created = await PostReportAsync(ownerToken);

        var response = await _client.SendAsync(
            AuthedRequest(HttpMethod.Get, created.Headers.Location!.ToString(), otherToken));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _processor.Release.TrySetResult();
    }

    private async Task<HttpResponseMessage> PostReportAsync(string token)
    {
        var request = AuthedRequest(
            HttpMethod.Post,
            "/api/background-jobs/quote-author-reports",
            token);
        request.Content = JsonContent.Create(new CreateQuoteAuthorReportRequest());
        return await _client.SendAsync(request);
    }

    private async Task<JsonElement> GetStatusAsync(Uri location, string token)
    {
        var response = await _client.SendAsync(
            AuthedRequest(HttpMethod.Get, location.ToString(), token));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> WaitForStatusAsync(
        Uri location,
        string token,
        string expectedStatus)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (!timeout.IsCancellationRequested)
        {
            var status = await GetStatusAsync(location, token);
            if (status.GetProperty("status").GetString() == expectedStatus)
                return status;

            await Task.Delay(10, timeout.Token);
        }

        throw new TimeoutException($"Job did not reach {expectedStatus}.");
    }

    private async Task<(int UserId, string Token)> CreateAuthenticatedUserAsync()
    {
        using var scope = _application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesApi.Data.QuotesDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var user = new User
        {
            Email = $"background-job-{Guid.NewGuid():N}@example.com",
            PasswordHash = "unused-in-these-tests",
            CreatedAt = _factory.Clock.UtcNow.UtcDateTime
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return (user.Id, authService.GenerateAccessToken(user));
    }

    private static HttpRequestMessage AuthedRequest(
        HttpMethod method,
        string url,
        string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private sealed class ControlledProcessor : IQuoteAuthorReportProcessor
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<QuoteAuthorReportResult> ProcessAsync(
            QuoteAuthorReportJob job,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);

            return new QuoteAuthorReportResult(
                3,
                2,
                [new QuoteAuthorCount("Marcus Aurelius", 2)]);
        }
    }
}
