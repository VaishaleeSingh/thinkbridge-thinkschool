using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Caching;
using QuotesApi.Observability;

namespace Quotes.Tests.Integration;

/// <summary>
/// Correctness of the cached read: hits, invalidation on write, the deep-page
/// bypass, and the response staying identical to the uncached one.
///
/// Speed is not asserted anywhere here. A test that asserts a cached read is
/// faster is a test that fails on a loaded CI machine for reasons unrelated to
/// the code. What is asserted instead is the thing speed is a proxy for: the
/// database was not touched.
/// </summary>
public class QuoteListCacheTests : IAsyncLifetime
{
    private CachedQuotesApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _token = null!;

    public async Task InitializeAsync()
    {
        _factory = new CachedQuotesApiFactory();
        _client = _factory.CreateClient();
        _token = await OutboxAtomicityTests.IssueTokenAsync(_factory);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    private HttpRequestMessage Authed(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    // 50, not 20. The seed is 20 quotes, so a newly created 21st lands on page
    // TWO at size 20 -- which is how the first version of the invalidation test
    // failed: the cache had correctly invalidated and the new quote was simply
    // not on the page being asserted. The bug was in the assertion.
    private const int PageSize = 50;

    private Task<HttpResponseMessage> GetPageAsync(int page = 1, int size = PageSize) =>
        _client.SendAsync(Authed(HttpMethod.Get, $"/api/quotes?page={page}&size={size}"));

    private DbCommandCounterInterceptor DbCommands =>
        _factory.Services.GetRequiredService<DbCommandCounterInterceptor>();

    private CacheMetrics Metrics =>
        _factory.Services.GetRequiredService<CacheMetrics>();

    [Fact]
    public async Task A_second_read_of_the_same_page_does_not_touch_the_database()
    {
        DbCommands.Reset();

        using var first = await GetPageAsync();
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterFirst = DbCommands.CountFor(CacheKeys.QuoteListFamily);
        afterFirst.Should().BeGreaterThan(0, "the first read is a miss and must load");

        using var second = await GetPageAsync();
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        DbCommands.CountFor(CacheKeys.QuoteListFamily).Should().Be(afterFirst, "the second read is a hit");
    }

    [Fact]
    public async Task A_cached_response_is_byte_identical_to_the_uncached_one()
    {
        // The first response comes from the factory, the second from the cache.
        // If they differ, the cache has quietly changed the API contract -- a
        // breaking change disguised as an optimisation, which is exactly what
        // caching the EF entity instead of a DTO would have produced.
        using var miss = await GetPageAsync();
        using var hit = await GetPageAsync();

        var missBody = await miss.Content.ReadAsStringAsync();
        var hitBody = await hit.Content.ReadAsStringAsync();

        hitBody.Should().Be(missBody);
    }

    [Fact]
    public async Task Creating_a_quote_invalidates_the_cached_pages()
    {
        using var beforeWrite = await GetPageAsync();
        var before = await beforeWrite.Content.ReadAsStringAsync();

        using var warm = await GetPageAsync();                 // hit, proving it was cached
        (await warm.Content.ReadAsStringAsync()).Should().Be(before);

        var create = Authed(HttpMethod.Post, "/api/quotes");
        create.Content = JsonContent.Create(new
        {
            author = "Cache Invalidation",
            text = "A create can shift every page, so every page is stale."
        });

        using var created = await _client.SendAsync(create);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var missesBefore = Metrics.Misses;

        using var afterWrite = await GetPageAsync();
        var after = await afterWrite.Content.ReadAsStringAsync();

        Metrics.Misses.Should().Be(missesBefore + 1, "the generation bumped, so the old key is unreachable");
        after.Should().NotBe(before, "the total changed, so the response must have");
        after.Should().Contain("Cache Invalidation");
    }

    [Fact]
    public async Task Deep_pages_are_not_cached()
    {
        // CacheOptions.MaxCachedPage is 20 here. `page` is unbounded by the
        // endpoint's validation, so without this policy a caller walking the
        // page number would mint cache entries without limit -- each one empty,
        // none ever read again. Asserting the bypass is asserting that hole is
        // closed.
        DbCommands.Reset();
        var keysBefore = Metrics.DistinctKeys;

        using var first = await GetPageAsync(page: 500, size: 20);
        using var second = await GetPageAsync(page: 500, size: 20);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        DbCommands.CountFor(CacheKeys.QuoteListFamily).Should().Be(
            4, "both reads bypassed the cache: two page reads at two commands each");

        Metrics.Bypasses.Should().BeGreaterOrEqualTo(2);
        Metrics.DistinctKeys.Should().Be(keysBefore, "a bypassed read must not mint a key");
    }

    [Fact]
    public async Task An_oversized_page_size_is_rejected_before_a_key_is_minted()
    {
        // Validation runs before key construction, and that ordering is the
        // difference between a cache and a memory-exhaustion vector: an
        // unbounded `size` would otherwise become an unbounded number of keys.
        var keysBefore = Metrics.DistinctKeys;

        using var response = await GetPageAsync(page: 1, size: 100_000);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Metrics.DistinctKeys.Should().Be(keysBefore);
    }

    [Fact]
    public async Task Different_page_sizes_are_different_entries()
    {
        DbCommands.Reset();

        using var twenty = await GetPageAsync(page: 1, size: 20);
        using var ten = await GetPageAsync(page: 1, size: 10);
        using var twentyAgain = await GetPageAsync(page: 1, size: 20);

        twenty.StatusCode.Should().Be(HttpStatusCode.OK);
        ten.StatusCode.Should().Be(HttpStatusCode.OK);

        DbCommands.CountFor(CacheKeys.QuoteListFamily).Should().Be(
            4, "size=20 and size=10 are separate keys, and the third read hits");
    }
}
