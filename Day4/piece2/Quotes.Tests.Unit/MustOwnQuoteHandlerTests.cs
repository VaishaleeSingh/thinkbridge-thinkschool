using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using QuotesApi.Authorization;
using QuotesApi.Models;

namespace Quotes.Tests.Unit;

public class MustOwnQuoteHandlerTests
{
    private static AuthorizationHandlerContext BuildContext(Quote resource, ClaimsPrincipal user)
    {
        var requirements = new[] { new MustOwnQuoteRequirement() };
        return new AuthorizationHandlerContext(requirements, user, resource);
    }

    private static ClaimsPrincipal UserWithClaim(string claimType, string claimValue)
    {
        var identity = new ClaimsIdentity(new[] { new Claim(claimType, claimValue) }, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task HandleAsync_WithLegacyQuoteHavingNoOwner_Succeeds()
    {
        // Arrange
        var handler = new MustOwnQuoteHandler();
        var quote = new Quote { Id = 1, Author = "A", Text = "T", CreatedByUserId = null };
        var caller = UserWithClaim("sub", "user-1");
        var context = BuildContext(quote, caller);

        // Act
        await handler.HandleAsync(context);

        // Assert
        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenCallerSubClaimMatchesOwner_Succeeds()
    {
        // Arrange
        var handler = new MustOwnQuoteHandler();
        var quote = new Quote { Id = 1, Author = "A", Text = "T", CreatedByUserId = "user-1" };
        var caller = UserWithClaim("sub", "user-1");
        var context = BuildContext(quote, caller);

        // Act
        await handler.HandleAsync(context);

        // Assert
        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenCallerNameIdentifierClaimMatchesOwner_Succeeds()
    {
        // Arrange
        var handler = new MustOwnQuoteHandler();
        var quote = new Quote { Id = 1, Author = "A", Text = "T", CreatedByUserId = "user-1" };
        var caller = UserWithClaim(ClaimTypes.NameIdentifier, "user-1");
        var context = BuildContext(quote, caller);

        // Act
        await handler.HandleAsync(context);

        // Assert
        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenCallerIsADifferentUser_Fails()
    {
        // Arrange
        var handler = new MustOwnQuoteHandler();
        var quote = new Quote { Id = 1, Author = "A", Text = "T", CreatedByUserId = "user-1" };
        var caller = UserWithClaim("sub", "user-2");
        var context = BuildContext(quote, caller);

        // Act
        await handler.HandleAsync(context);

        // Assert
        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_WhenCallerHasNoIdentifierClaimAtAll_Fails()
    {
        // Arrange
        var handler = new MustOwnQuoteHandler();
        var quote = new Quote { Id = 1, Author = "A", Text = "T", CreatedByUserId = "user-1" };
        var caller = new ClaimsPrincipal(new ClaimsIdentity(Array.Empty<Claim>(), "TestAuth"));
        var context = BuildContext(quote, caller);

        // Act
        await handler.HandleAsync(context);

        // Assert
        context.HasSucceeded.Should().BeFalse();
    }
}
