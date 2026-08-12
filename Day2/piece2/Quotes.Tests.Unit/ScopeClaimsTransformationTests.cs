using System.Security.Claims;
using FluentAssertions;
using QuotesApi.Authorization;

namespace Quotes.Tests.Unit;

public class ScopeClaimsTransformationTests
{
    [Fact]
    public async Task TransformAsync_WhenPrincipalAlreadyHasScopeClaim_DoesNotAddMore()
    {
        // Arrange
        var transformation = new ScopeClaimsTransformation();
        var identity = new ClaimsIdentity(
            new[] { new Claim("scope", "quotes.read") },
            "CustomJwt");
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await transformation.TransformAsync(principal);

        // Assert
        result.FindAll("scope").Should().ContainSingle(c => c.Value == "quotes.read");
    }

    [Fact]
    public async Task TransformAsync_WithScpClaim_SplitsIntoMultipleScopeClaims()
    {
        // Arrange
        var transformation = new ScopeClaimsTransformation();
        var identity = new ClaimsIdentity(
            new[] { new Claim("scp", "quotes.read quotes.write") },
            "EntraId");
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await transformation.TransformAsync(principal);

        // Assert
        result.FindAll("scope").Select(c => c.Value)
            .Should().BeEquivalentTo(new[] { "quotes.read", "quotes.write" });
    }

    [Fact]
    public async Task TransformAsync_WithRolesClaims_EachBecomesAScopeClaim()
    {
        // Arrange
        var transformation = new ScopeClaimsTransformation();
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim("roles", "quotes.read"),
                new Claim("roles", "quotes.delete")
            },
            "EntraId");
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await transformation.TransformAsync(principal);

        // Assert
        result.FindAll("scope").Select(c => c.Value)
            .Should().BeEquivalentTo(new[] { "quotes.read", "quotes.delete" });
    }

    [Fact]
    public async Task TransformAsync_WithASingleRolesClaim_DoesNotThrow()
    {
        // Arrange
        // Regression test: identity.FindAll("roles") is a lazy view over
        // the identity's own claims, and the production code used to
        // call identity.AddClaim(...) while still enumerating that same
        // view -- which throws InvalidOperationException the moment
        // .NET's enumerator re-checks the collection's version. This
        // reproduced with as few as ONE "roles" claim, meaning any Entra
        // ID application-permission token would have crashed every
        // request during claims transformation, before authorization
        // even ran. Fixed by snapshotting FindAll("roles") with ToList()
        // before mutating.
        var transformation = new ScopeClaimsTransformation();
        var identity = new ClaimsIdentity(
            new[] { new Claim("roles", "quotes.read") },
            "EntraId");
        var principal = new ClaimsPrincipal(identity);

        // Act
        var act = () => transformation.TransformAsync(principal);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TransformAsync_WithNoScpOrRolesOrScopeClaims_AddsNothing()
    {
        // Arrange
        var transformation = new ScopeClaimsTransformation();
        var identity = new ClaimsIdentity(
            new[] { new Claim("email", "user@thinkbridge.com") },
            "EntraId");
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await transformation.TransformAsync(principal);

        // Assert
        result.FindAll("scope").Should().BeEmpty();
    }

    [Fact]
    public async Task TransformAsync_WithUnauthenticatedIdentity_LeavesPrincipalUntouched()
    {
        // Arrange
        var transformation = new ScopeClaimsTransformation();
        // No authenticationType passed in -> IsAuthenticated is false.
        var identity = new ClaimsIdentity(new[] { new Claim("scp", "quotes.read") });
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await transformation.TransformAsync(principal);

        // Assert
        result.FindAll("scope").Should().BeEmpty();
    }
}
