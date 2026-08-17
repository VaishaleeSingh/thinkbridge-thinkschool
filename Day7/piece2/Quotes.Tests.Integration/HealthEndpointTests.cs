using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Quotes.Tests.Integration;

/// <summary>
/// The health endpoints exist so an orchestrator can decide whether to
/// restart a container or stop routing to it. Those decisions are only ever
/// exercised in production, which is precisely why the endpoints are worth
/// testing here -- a readiness probe that silently stopped checking the
/// database would look identical from the outside until the day it mattered.
/// </summary>
public class HealthEndpointTests : IAsyncLifetime
{
    private QuotesApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new QuotesApiFactory();
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_AreReachableWithoutAToken(string path)
    {
        // No Authorization header is set anywhere in this test. A probe has
        // no credentials to offer, so a health endpoint that could return
        // 401 would be worse than useless -- it would report every healthy
        // container as unhealthy.
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_IdentifiesTheServiceAndReportsItsChecks()
    {
        var response = await _client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        // Naming the service is the difference between "something answered"
        // and "the application answered". A proxy or a stray container on
        // the same port would satisfy the status code but not this.
        root.GetProperty("service").GetString().Should().Be("QuotesApi");
        root.GetProperty("status").GetString().Should().Be("Healthy");

        var checks = root.GetProperty("checks").EnumerateArray().ToList();
        checks.Should().ContainSingle(check =>
            check.GetProperty("name").GetString() == "database");
    }

    [Fact]
    public async Task Live_RunsNoChecks_SoADatabaseProblemCannotRestartTheContainer()
    {
        var response = await _client.GetAsync("/health/live");
        var body = await response.Content.ReadAsStringAsync();

        using var json = JsonDocument.Parse(body);

        // This is the assertion that actually protects the design. If
        // someone later "tidies up" by pointing all three endpoints at the
        // same options, the database check would start running here, and a
        // transient database failure would begin restarting healthy
        // containers. An empty check list is the whole point of /health/live.
        json.RootElement.GetProperty("checks").EnumerateArray().Should().BeEmpty();
        json.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
    }

    [Fact]
    public async Task Ready_RunsTheDatabaseCheck()
    {
        var response = await _client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        using var json = JsonDocument.Parse(body);

        json.RootElement.GetProperty("checks").EnumerateArray()
            .Should().ContainSingle(check =>
                check.GetProperty("name").GetString() == "database");
    }

    [Fact]
    public async Task Health_DoesNotLeakExceptionDetail()
    {
        // Health endpoints are unauthenticated. A failing database check
        // whose exception message reached the response would hand a
        // connection string to anyone who asked, so the writer reports a
        // boolean instead. This asserts the shape rather than the failure
        // path -- the guarantee is that there is nowhere for a message to
        // appear even if a check does fail.
        var response = await _client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain("Data Source");
        body.Should().NotContain("Exception");

        using var json = JsonDocument.Parse(body);
        foreach (var check in json.RootElement.GetProperty("checks").EnumerateArray())
        {
            check.GetProperty("error").ValueKind
                .Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
        }
    }
}
