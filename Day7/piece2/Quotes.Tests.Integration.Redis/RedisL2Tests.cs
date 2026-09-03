using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using QuotesApi.Caching;
using StackExchange.Redis;

namespace Quotes.Tests.Integration.Redis;

/// <summary>
/// The one claim that cannot be made without a real distributed cache: that L2
/// is genuinely SHARED between instances.
///
/// Everything else about Day 21 -- stampede protection, the drop in database
/// commands, graceful degradation when Redis is unreachable -- is measured
/// without Docker in Quotes.Tests.Integration. This suite exists for the half
/// of "in-memory + Redis" that the in-process tests cannot reach.
///
/// HOW IT IS PROVED. Two hosts, one Redis, and DIFFERENT DATABASES with
/// different data. If host B answers with host A's payload -- a payload that
/// host B's own database could not have produced -- the entry can only have
/// come from Redis. Two hosts sharing a database would prove nothing: a "hit"
/// on the second host would be indistinguishable from it reading the same rows.
/// </summary>
[Collection(RedisCollection.Name)]
public class RedisL2Tests(RedisFixture redis)
{
    private const string Page = "/api/quotes?page=1&size=20";

    /// <summary>
    /// Both hosts start from DbInitializer's seed, which is 20 quotes.
    /// </summary>
    private const int SeededQuotes = 20;

    private static HttpRequestMessage Authed(string token, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<int> ReadTotalAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("total").GetInt32();
    }

    /// <summary>
    /// Clears Redis so one test cannot inherit another's generation token or
    /// entries. The collection runs sequentially, so this is deterministic --
    /// and it is done rather than assumed, because a shared container is
    /// exactly the kind of state that makes a suite pass in isolation and fail
    /// in a full run.
    /// </summary>
    private async Task FlushAsync()
    {
        var options = ConfigurationOptions.Parse(redis.ConnectionString);
        options.AllowAdmin = true;

        await using var connection = await ConnectionMultiplexer.ConnectAsync(options);
        foreach (var endpoint in connection.GetEndPoints())
            await connection.GetServer(endpoint).FlushDatabaseAsync();
    }

    [Fact]
    public async Task An_entry_written_by_one_host_is_served_to_another()
    {
        await FlushAsync();

        await using var hostA = new RedisCachedQuotesApiFactory(redis.ConnectionString);
        await using var hostB = new RedisCachedQuotesApiFactory(redis.ConnectionString);

        using var clientA = hostA.CreateClient();
        using var clientB = hostB.CreateClient();

        var tokenA = await hostA.IssueTokenAsync();
        var tokenB = await hostB.IssueTokenAsync();

        // Make the two hosts' data differ, without telling either cache. Going
        // through POST /api/quotes would bump the generation and invalidate the
        // entry this test is about to check.
        await hostB.AddQuoteDirectlyAsync("Host B Only", "This quote exists only in host B's database.");

        // A loads from its own database and writes the entry to L2.
        using var fromA = await clientA.SendAsync(Authed(tokenA, Page));
        fromA.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadTotalAsync(fromA)).Should().Be(SeededQuotes);

        // B has never read this page. Its L1 is empty and its own database
        // holds one more quote, so a database read would answer 21.
        using var fromB = await clientB.SendAsync(Authed(tokenB, Page));
        fromB.StatusCode.Should().Be(HttpStatusCode.OK);

        (await ReadTotalAsync(fromB)).Should().Be(
            SeededQuotes,
            "host B answered with host A's payload, which its own database could not have produced -- "
            + "so the entry came from Redis");

        hostB.GetService<CacheMetrics>().Misses.Should().Be(
            0, "host B never ran the factory: L2 answered before the database was consulted");
    }

    [Fact]
    public async Task A_write_on_one_host_invalidates_the_entry_for_the_other()
    {
        await FlushAsync();

        await using var hostA = new RedisCachedQuotesApiFactory(redis.ConnectionString);
        await using var hostB = new RedisCachedQuotesApiFactory(redis.ConnectionString);

        using var clientA = hostA.CreateClient();
        using var clientB = hostB.CreateClient();

        var tokenA = await hostA.IssueTokenAsync();
        var tokenB = await hostB.IssueTokenAsync();

        using var warmA = await clientA.SendAsync(Authed(tokenA, Page));
        warmA.StatusCode.Should().Be(HttpStatusCode.OK);

        using var hitB = await clientB.SendAsync(Authed(tokenB, Page));
        hitB.StatusCode.Should().Be(HttpStatusCode.OK);
        hostB.GetService<CacheMetrics>().Misses.Should().Be(0, "served from the shared entry");

        // A write on host A. It bumps the generation token in Redis, which is
        // part of every key -- so the entry both hosts were sharing becomes
        // unaddressable rather than merely deleted.
        //
        // This is why the generation lives in the key instead of using
        // RemoveByTagAsync: a tag removal could clear Redis but could not reach
        // into host B's in-process memory. Changing the key reaches both.
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/quotes")
        {
            Content = JsonContent.Create(new
            {
                author = "Cross Instance",
                text = "A write on one instance must invalidate the other's cached pages."
            })
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);

        using var created = await clientA.SendAsync(create);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var missesBefore = hostB.GetService<CacheMetrics>().Misses;

        using var afterWrite = await clientB.SendAsync(Authed(tokenB, Page));
        afterWrite.StatusCode.Should().Be(HttpStatusCode.OK);

        hostB.GetService<CacheMetrics>().Misses.Should().Be(
            missesBefore + 1,
            "the generation changed, so host B's next read addressed a key nothing had written");

        // And it read its OWN database -- which never saw host A's write, since
        // the two hosts have separate databases. Host B is not meant to see the
        // new quote; it is meant to stop serving a stale page.
        (await ReadTotalAsync(afterWrite)).Should().Be(SeededQuotes);
    }

    [Fact]
    public async Task The_entry_lands_in_redis_under_the_designed_key()
    {
        await FlushAsync();

        await using var host = new RedisCachedQuotesApiFactory(redis.ConnectionString);
        using var client = host.CreateClient();
        var token = await host.IssueTokenAsync();

        using var response = await client.SendAsync(Authed(token, Page));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var options = ConfigurationOptions.Parse(redis.ConnectionString);
        options.AllowAdmin = true;

        await using var connection = await ConnectionMultiplexer.ConnectAsync(options);
        var server = connection.GetServer(connection.GetEndPoints().Single());

        var afterRead = server.Keys(pattern: "*").Select(key => key.ToString()).ToList();

        // Asserted rather than eyeballed in redis-cli, because the key shape is
        // load-bearing: the version segment stops a new build reading an old
        // entry, and the generation segment IS the invalidation mechanism. A
        // key that quietly lost either would still cache -- and would serve
        // stale or wrongly-shaped data indefinitely.
        //
        // The full expected key is quotes:list:v1:g{token}:p1:s20 -- the
        // leading segment from InstanceName, the rest from CacheKeys.
        afterRead.Should().Contain(
            key => key == $"{RedisCachedQuotesApiFactory.KeyPrefix}list:{CacheKeys.Version}:g"
                          + $"{CacheKeys.InitialGeneration}:p1:s20",
            "a read on a cache nothing has invalidated is addressed at the initial generation");

        // A WRITE is what puts the generation token in Redis. The first version
        // of this test asserted the token was already there after a read, which
        // it is not and should not be: GetAsync only reads it, and writing a
        // token on every read would be a distributed write to record that
        // nothing had changed.
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/quotes")
        {
            Content = JsonContent.Create(new
            {
                author = "Generation Token",
                text = "A write is what persists the generation token to the shared store."
            })
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var created = await client.SendAsync(create);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var afterWrite = server.Keys(pattern: "*").Select(key => key.ToString()).ToList();

        afterWrite.Should().Contain(
            key => key == $"{RedisCachedQuotesApiFactory.KeyPrefix}{CacheKeys.GenerationKey}",
            "the token must reach the shared store, or instances cannot agree on which keys are current");

        afterWrite.Should().OnlyContain(
            key => key.StartsWith(RedisCachedQuotesApiFactory.KeyPrefix),
            "every key carries the instance prefix exactly once, so a Redis shared with anything else "
            + "stays legible and FLUSHDB is never the only way to clear this app's data");
    }
}
