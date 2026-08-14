using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Data;
using QuotesApi.Models;

namespace Quotes.Tests.Integration.SqlServer;

/// <summary>
/// SQL Server's default collation (SQL_Latin1_General_CP1_CI_AS) is
/// case-insensitive; SQLite's default text comparison is case-sensitive
/// unless a column explicitly opts into COLLATE NOCASE. QuotesDbContext
/// never sets an explicit collation on User.Email, so the unique index on
/// it behaves differently depending on which engine is actually running --
/// exactly the kind of bug Quotes.Tests.Integration's SQLite backend
/// cannot surface: two emails differing only by case would silently both
/// succeed there.
/// </summary>
[Collection("SqlServer")]
public class SqlServerCollationTests : IAsyncLifetime
{
    private readonly MsSqlContainerFixture _containerFixture;
    private SqlServerQuotesApiFactory _factory = null!;

    public SqlServerCollationTests(MsSqlContainerFixture containerFixture)
    {
        _containerFixture = containerFixture;
    }

    public async Task InitializeAsync()
    {
        _factory = new SqlServerQuotesApiFactory(_containerFixture.ConnectionString);
        using var _ = _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task DuplicateEmail_DifferingOnlyByCase_ViolatesUniqueIndexUnderSqlServersDefaultCollation()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();

        db.Users.Add(new User
        {
            Email = "person@example.com",
            PasswordHash = "unused-in-this-test",
            CreatedAt = _factory.Clock.UtcNow.UtcDateTime
        });
        await db.SaveChangesAsync();

        db.Users.Add(new User
        {
            Email = "PERSON@EXAMPLE.COM",
            PasswordHash = "unused-in-this-test",
            CreatedAt = _factory.Clock.UtcNow.UtcDateTime
        });

        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
