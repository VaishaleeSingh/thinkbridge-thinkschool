using FluentAssertions;
using QuotesApi.Caching;

namespace Quotes.Tests.Unit.Caching;

/// <summary>
/// The in-process generation token, which is what single-instance deployments
/// and the whole in-process test suite run on.
/// </summary>
public class CacheGenerationTests
{
    [Fact]
    public async Task Starts_at_the_shared_initial_token()
    {
        // Not a random value. If a fresh instance minted its own token, two
        // instances on a cold cache would address different keys and neither
        // would ever hit the other's entries -- a shared cache behaving like N
        // private ones, at N times the database load it was meant to remove.
        var generation = new InMemoryCacheGeneration();

        (await generation.GetAsync()).Should().Be(CacheKeys.InitialGeneration);
    }

    [Fact]
    public async Task A_bump_changes_the_token()
    {
        var generation = new InMemoryCacheGeneration();
        var before = await generation.GetAsync();

        await generation.BumpAsync();

        (await generation.GetAsync()).Should().NotBe(before);
    }

    [Fact]
    public async Task Concurrent_bumps_are_not_lost()
    {
        // Interlocked, not ++. A lost bump means a write whose invalidation
        // silently did not happen.
        var generation = new InMemoryCacheGeneration();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => generation.BumpAsync().AsTask()));

        (await generation.GetAsync()).Should().Be("200");
    }
}
