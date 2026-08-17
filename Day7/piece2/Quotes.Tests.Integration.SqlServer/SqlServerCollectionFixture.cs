namespace Quotes.Tests.Integration.SqlServer;

/// <summary>
/// xUnit collection definition tying every SQL-Server test class to the
/// SAME MsSqlContainerFixture instance, so the container starts once for
/// the whole run (the first time any test in the collection needs it)
/// and stops once at the end -- not once per test class.
/// </summary>
[CollectionDefinition("SqlServer")]
public class SqlServerCollectionFixture : ICollectionFixture<MsSqlContainerFixture>
{
}
