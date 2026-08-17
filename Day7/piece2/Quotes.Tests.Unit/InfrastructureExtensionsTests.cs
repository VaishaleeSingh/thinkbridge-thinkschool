using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using QuotesApi.Configuration;
using QuotesApi.Extensions;

namespace Quotes.Tests.Unit;

/// <summary>
/// AddInfrastructure() wires up the database, small helper services, and the
/// dual CustomJwt/EntraId authentication scheme in one method. Almost
/// everything in it is registration -- calling AddDbContext/AddScoped never
/// touches a live database or network, so it is not worth a test per line.
///
/// What IS worth testing is the configuration contract, and it changed
/// shape in this piece. It used to be a hand-written "if the secret is
/// missing, throw" executed while registering services. It is now
/// ValidateDataAnnotations().ValidateOnStart() on the bound options, which
/// moves the failure to host startup and covers every rule at once instead
/// of only the first one someone thought to check.
///
/// That difference matters in a way worth stating: registration succeeding
/// no longer means the configuration is good. The failure surfaces when the
/// host starts -- which is what these tests drive.
/// </summary>
public class InfrastructureExtensionsTests
{
    private static IConfiguration ConfigWith(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> ValidJwtConfig() => new()
    {
        ["Jwt:Secret"] = "unit-test-secret-that-is-long-enough-for-hmacsha256",
        ["Jwt:Issuer"] = "https://issuer.under.test",
        ["Jwt:Audience"] = "audience-under-test",
        ["Jwt:AccessTokenLifetime"] = "00:15:00"
    };

    /// <summary>
    /// Resolving IOptions&lt;T&gt; from a container built by AddInfrastructure
    /// runs the same validation the host runs at startup, without needing a
    /// real host.
    /// </summary>
    private static JwtOptions ResolveJwtOptions(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(ConfigWith(values));
        return services.BuildServiceProvider()
            .GetRequiredService<IOptions<JwtOptions>>().Value;
    }

    [Fact]
    public void JwtOptions_WithNoSecretConfigured_FailsValidationWithAnActionableMessage()
    {
        var values = ValidJwtConfig();
        values.Remove("Jwt:Secret");

        var act = () => ResolveJwtOptions(values);

        // The message has to tell whoever hits this what to actually do --
        // this is the error a developer sees on a fresh clone, and the one
        // a deployment sees when a Key Vault reference is misconfigured.
        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Jwt:Secret*");
    }

    [Fact]
    public void JwtOptions_WithATooShortSecret_FailsValidation()
    {
        // HMAC-SHA256 needs a 256-bit key. Without this rule a short secret
        // is accepted at startup and throws from deep inside the token
        // handler the first time somebody logs in -- an error message about
        // key sizes, arriving at the worst possible moment, pointing at
        // library code rather than at the setting that caused it.
        var values = ValidJwtConfig();
        values["Jwt:Secret"] = "too-short";

        var act = () => ResolveJwtOptions(values);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*32 characters*");
    }

    [Fact]
    public void JwtOptions_BindsTheAccessTokenLifetimeFromADurationString()
    {
        var values = ValidJwtConfig();
        values["Jwt:AccessTokenLifetime"] = "00:42:00";

        ResolveJwtOptions(values).AccessTokenLifetime.Should().Be(TimeSpan.FromMinutes(42));
    }

    [Fact]
    public void JwtOptions_BindsIssuerAndAudienceFromConfiguration()
    {
        var options = ResolveJwtOptions(ValidJwtConfig());

        options.Issuer.Should().Be("https://issuer.under.test");
        options.Audience.Should().Be("audience-under-test");
    }

    [Fact]
    public void PaginationOptions_DefaultsToOneHundred_WhenTheSectionIsAbsent()
    {
        // Absent configuration must not mean "zero" -- a MaxPageSize of 0
        // would reject every request. Defaults live on the options type
        // precisely so a missing section degrades to something sensible.
        var services = new ServiceCollection();
        services.AddInfrastructure(ConfigWith(ValidJwtConfig()));

        var pagination = services.BuildServiceProvider()
            .GetRequiredService<IOptions<PaginationOptions>>().Value;

        pagination.MaxPageSize.Should().Be(100);
    }
}
