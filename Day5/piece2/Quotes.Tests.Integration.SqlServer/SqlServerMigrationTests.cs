using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Data;

namespace Quotes.Tests.Integration.SqlServer;

[Collection("SqlServer")]
public class SqlServerMigrationTests : IAsyncLifetime
{
    private readonly MsSqlContainerFixture _containerFixture;
    private SqlServerQuotesApiFactory _factory = null!;

    public SqlServerMigrationTests(MsSqlContainerFixture containerFixture)
    {
        _containerFixture = containerFixture;
    }

    public async Task InitializeAsync()
    {
        _factory = new SqlServerQuotesApiFactory(_containerFixture.ConnectionString);

        // Force the host to actually build, which is what triggers
        // Program.cs's migrate-on-startup call -- CreateClient() is the
        // trigger, we just don't need the client itself for this test.
        using var _ = _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Factory_OnStartup_AppliesAllMigrationsToFreshSqlServerDatabase()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();

        var applied = await db.Database.GetAppliedMigrationsAsync();
        var all = db.Database.GetMigrations();

        all.Should().NotBeEmpty(
            "the SQL-Server-specific migrations must exist for this test to mean anything -- see QuotesApi.Migrations.SqlServer");
        applied.Should().BeEquivalentTo(all);
    }
}
