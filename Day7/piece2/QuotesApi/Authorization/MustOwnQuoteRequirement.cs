using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using QuotesApi.Models;

namespace QuotesApi.Authorization;

/// <summary>
/// Marker requirement: "the caller must be the user who created this
/// specific quote." It carries no data of its own — the actual rule lives
/// in MustOwnQuoteHandler below.
///
/// This is kept separate from the claim-based policies (can-read-quotes,
/// can-edit-quotes, can-delete-quotes) because those can be answered from
/// the token alone, before an endpoint even runs. This one can't: you
/// can't know whether someone owns "this quote" until "this quote" has
/// been loaded from the database. That's why it's applied imperatively
/// inside the DELETE endpoint rather than declared on the route.
/// </summary>
public class MustOwnQuoteRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// Resource-based authorization handler: given one specific Quote and the
/// caller's identity, decides whether the caller is allowed to act on it
/// because they created it.
///
/// Handlers like this are resolved from DI just like any other service —
/// if a real rule needed to look something up (e.g. check a ban list via
/// an injected repository), that dependency would simply be added as a
/// constructor parameter here.
/// </summary>
public class MustOwnQuoteHandler : AuthorizationHandler<MustOwnQuoteRequirement, Quote>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        MustOwnQuoteRequirement requirement,
        Quote resource)
    {
        // Custom JWTs carry the user id under "sub"; depending on how the
        // token was validated, that can surface as the standard
        // ClaimTypes.NameIdentifier claim or still as the raw "sub" claim
        // type — check both rather than assuming one.
        var callerId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst("sub")?.Value;

        if (callerId is not null && callerId == resource.CreatedByUserId)
            context.Succeed(requirement);

        // No explicit Fail() call needed: if Succeed() was never called,
        // the requirement stays unsatisfied and AuthorizeAsync(...) reports
        // Succeeded = false on its own.
        return Task.CompletedTask;
    }
}
