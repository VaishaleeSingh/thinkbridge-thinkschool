using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Extensions;

namespace Quotes.Tests.Unit;

/// <summary>
/// AddInfrastructure() wires up the database, small helper services, and
/// the dual CustomJwt/EntraId authentication scheme in one method (see that
/// file's own comments for the full picture). Almost everything in it is
/// registration -- calling AddDbContext/AddScoped/AddSingleton never
/// touches a live database or network, so it isn't worth a dedicated test
/// per line. The one real decision point is the fail-fast guard below: it
/// stops the app starting at all with a clear error, instead of failing
/// confusingly later the first time a token needs signing.
/// </summary>
public class InfrastructureExtensionsTests
{
    [Fact]
    public void AddInfrastructure_MissingJwtSecret_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        // Act
        var act = () => services.AddInfrastructure(configuration);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Jwt:Secret*");
    }
}
