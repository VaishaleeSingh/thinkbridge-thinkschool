using FluentAssertions;
using QuotesApi.Caching;

namespace Quotes.Tests.Unit.Caching;

/// <summary>
/// The key shape, pinned.
///
/// These read like tests of string formatting, and they are not. Every one of
/// them guards a property the cache's correctness rests on, and each would fail
/// silently in production rather than loudly: a key that omits the generation
/// serves stale data for ever, a key that omits the version lets a new build
/// read an old contract, and a key that does not vary by page serves page 1 to
/// everyone asking for page 7.
/// </summary>
public class CacheKeysTests
{
    [Fact]
    public void The_key_carries_the_version_the_generation_and_both_inputs()
    {
        // No app prefix here: CacheOptions.Redis.InstanceName prepends
        // "quotes:" to everything the distributed cache writes, so adding it
        // here too produced "quotes:quotes:list:..." -- one job done twice,
        // found by the Redis suite reading a real key.
        CacheKeys.QuoteList("7", page: 3, size: 20)
            .Should().Be("list:v1:g7:p3:s20");
    }

    [Fact]
    public void A_different_generation_is_a_different_key()
    {
        // This IS the invalidation mechanism. If a bump produced the same key,
        // a write would appear to invalidate and the endpoint would serve the
        // pre-write page until natural expiry, with nothing logged.
        CacheKeys.QuoteList("1", 1, 20)
            .Should().NotBe(CacheKeys.QuoteList("2", 1, 20));
    }

    [Theory]
    [InlineData(1, 20, 2, 20)]   // page differs
    [InlineData(1, 20, 1, 10)]   // size differs
    public void Different_inputs_are_different_keys(int pageA, int sizeA, int pageB, int sizeB)
    {
        CacheKeys.QuoteList("0", pageA, sizeA)
            .Should().NotBe(CacheKeys.QuoteList("0", pageB, sizeB));
    }

    [Fact]
    public void The_same_inputs_are_the_same_key()
    {
        // Without this there are no hits at all, only writes -- a cache that
        // costs memory and returns nothing.
        CacheKeys.QuoteList("0", 1, 20)
            .Should().Be(CacheKeys.QuoteList("0", 1, 20));
    }

    [Fact]
    public void The_generation_key_and_the_entry_keys_share_the_contract_version()
    {
        // A bump must invalidate the entries it is meant to. If the generation
        // key were versioned separately, a version bump would leave the old
        // generation token addressing the new keys.
        CacheKeys.GenerationKey.Should().Contain(CacheKeys.Version);
        CacheKeys.QuoteList("0", 1, 1).Should().Contain(CacheKeys.Version);
    }
}
