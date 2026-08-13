using Testcontainers.MsSql;

namespace Quotes.Tests.Integration.SqlServer;

/// <summary>
/// One real SQL Server 2022 container for the whole test run, not one per
/// test -- starting a full SQL Server instance takes real seconds (or
/// minutes on the very first run, while Docker pulls the image), so
/// paying that cost once per collection instead of once per test is the
/// difference between a suite that runs in seconds and one that takes
/// minutes.
///
/// Per-test isolation still holds even though every test shares this one
/// container: SqlServerQuotesApiFactory points each test at its own
/// uniquely named database on it (see that class), so no two tests ever
/// see each other's rows.
/// </summary>
public class MsSqlContainerFixture : IAsyncLifetime
{
    // "2022-latest" is a floating tag -- Microsoft moves it to newer
    // cumulative updates over time, which means a test run today and the
    // same run in six months could pull a different image without any
    // code here changing. That is a real reproducibility tradeoff: if
    // this suite ever fails in a way that looks environment-specific
    // rather than code-specific, pin this to a specific CU tag (e.g.
    // "2022-CU<N>-ubuntu-22.04") instead. Left as the floating tag here
    // deliberately, not by oversight: this sandbox has no network path to
    // check which CU tags currently exist, and shipping a guessed,
    // possibly-nonexistent tag would fail every pull outright, which is a
    // worse failure mode than the reproducibility risk it would guard
    // against.
    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
