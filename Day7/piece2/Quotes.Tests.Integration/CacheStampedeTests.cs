using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Caching;
using QuotesApi.Observability;

namespace Quotes.Tests.Integration;

/// <summary>
/// The Day 21 headline: a cache miss under concurrency must not fan out into N
/// identical database hits.
///
/// The pair of tests matters more than either one. Asserting "100 concurrent
/// requests caused 2 database commands" on its own proves the number is 2 --
/// not that it used to be 200. The control test runs the identical load with
/// the cache off and asserts the fan-out, so the two together are a
/// measurement rather than a number.
///
/// WHY THE EXPECTED COUNT IS 2 AND NOT 1: one page read costs two round trips,
/// a COUNT(*) and a paged SELECT (see QuoteRepository.GetPagedAsync). The unit
/// of "one database hit" for this endpoint is therefore two commands. Asserting
/// 1 would have been asserting a misunderstanding of the code under test.
/// </summary>
public class CacheStampedeTests
{
    private const int Concurrency = 100;
    private const int CommandsPerPageRead = 2;   // COUNT(*) + paged SELECT

    private static HttpRequestMessage Authed(string token, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Cold_cache_under_100_concurrent_requests_hits_the_database_once()
    {
        await using var factory = new CachedQuotesApiFactory();
        using var client = factory.CreateClient();
        var token = await OutboxAtomicityTests.IssueTokenAsync(factory);

        var dbCommands = factory.Services.GetRequiredService<DbCommandCounterInterceptor>();
        var metrics = factory.Services.GetRequiredService<CacheMetrics>();

        // Zeroed AFTER the host has migrated and seeded, so the baseline is the
        // load and not the startup. Asserting on a delta instead would hide the
        // case where the counter is already wrong before the load begins.
        dbCommands.Reset();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, Concurrency)
                .Select(_ => client.SendAsync(Authed(token, "/api/quotes?page=1&size=20"))));

        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);

        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        bodies.Distinct().Should().HaveCount(1, "every caller must get the same page");

        dbCommands.CountFor(CacheKeys.QuoteListFamily).Should().Be(
            CommandsPerPageRead,
            "one factory invocation is shared by all {0} callers -- that is what stampede protection means",
            Concurrency);

        metrics.Misses.Should().Be(1);
        metrics.Hits.Should().Be(Concurrency - 1);

        // The number that makes the hit rate meaningful. One key, so a 99% hit
        // rate here is a statement about deduplication and NOT about how a real
        // traffic mix would behave.
        metrics.DistinctKeys.Should().Be(1);

        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task The_same_load_without_the_cache_fans_out_to_the_database()
    {
        // The control. Without it the test above proves only that the number is
        // small, not that it used to be large.
        //
        // Same factory type as the cached test, with the cache off -- so the
        // ONLY difference between the two runs is the cache. Using the plain
        // QuotesApiFactory here would also have changed the database from a
        // file to one shared in-memory connection, and that connection cannot
        // serve 100 concurrent commands: the first version of this test
        // produced a wall of 500s and looked like a cache bug.
        await using var factory = new CachedQuotesApiFactory(cacheEnabled: false);
        using var client = factory.CreateClient();
        var token = await OutboxAtomicityTests.IssueTokenAsync(factory);

        var dbCommands = factory.Services.GetRequiredService<DbCommandCounterInterceptor>();
        dbCommands.Reset();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, Concurrency)
                .Select(_ => client.SendAsync(Authed(token, "/api/quotes?page=1&size=20"))));

        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);

        dbCommands.CountFor(CacheKeys.QuoteListFamily).Should().Be(
            Concurrency * CommandsPerPageRead,
            "with no cache every request does its own COUNT and SELECT");

        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task Concurrent_requests_for_different_pages_each_load_once()
    {
        // Stampede protection is per KEY, not per endpoint. Ten pages requested
        // ten times each should be ten factory invocations, not one and not a
        // hundred -- a cache that collapsed distinct keys would be serving the
        // wrong page to somebody.
        await using var factory = new CachedQuotesApiFactory();
        using var client = factory.CreateClient();
        var token = await OutboxAtomicityTests.IssueTokenAsync(factory);

        var dbCommands = factory.Services.GetRequiredService<DbCommandCounterInterceptor>();
        var metrics = factory.Services.GetRequiredService<CacheMetrics>();
        dbCommands.Reset();

        var requests = Enumerable.Range(1, 10)
            .SelectMany(page => Enumerable.Repeat(page, 10))
            .Select(page => client.SendAsync(Authed(token, $"/api/quotes?page={page}&size=5")));

        var responses = await Task.WhenAll(requests);

        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);

        metrics.Misses.Should().Be(10, "one miss per distinct page");
        metrics.DistinctKeys.Should().Be(10);
        dbCommands.CountFor(CacheKeys.QuoteListFamily).Should().Be(10 * CommandsPerPageRead);

        foreach (var response in responses) response.Dispose();
    }
}
