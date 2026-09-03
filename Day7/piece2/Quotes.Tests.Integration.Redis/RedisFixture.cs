using Testcontainers.Redis;

namespace Quotes.Tests.Integration.Redis;

/// <summary>
/// One Redis container for the whole collection.
///
/// Lifetime: started before the first test, disposed after the last. A
/// container per test would be correct and unusably slow; sharing one is what
/// keeps this runnable in a feedback loop. The tests do not interfere with each
/// other because each uses distinct cache keys -- and where they cannot, they
/// flush explicitly rather than hoping.
///
/// redis:7-alpine, pinned. "redis:latest" is a test that changes underneath
/// you: a run that passes today and fails next month with no commit in between
/// is worse than no test, because the first thing it does is waste an
/// afternoon looking for a change that was never made.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    /// <summary>The connection string the app's cache is pointed at.</summary>
    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "redis";
}
