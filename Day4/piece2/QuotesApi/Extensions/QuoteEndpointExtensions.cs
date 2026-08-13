using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QuotesApi.Authorization;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using System.Security.Claims;

namespace QuotesApi.Extensions;

/// <summary>
/// All HTTP endpoints for /api/quotes live here. Each endpoint is a small
/// function: read the request, ask a repository/service to do the real
/// work, and translate the result into an HTTP response. There is no
/// database or business logic directly in this file — that lives in
/// QuoteRepository and friends.
///
/// As of Day 3 part 2, endpoints also declare exactly which permission
/// they need via .RequireAuthorization("policy-name") — see
/// InfrastructureExtensions.cs for what each policy checks. Being
/// authenticated at all is no longer enough by itself; being authenticated
/// AND holding the right scope is.
/// </summary>
public static class QuoteEndpointExtensions
{
    public static IEndpointRouteBuilder MapQuoteEndpoints(
        this IEndpointRouteBuilder app)
    {
        // MapGroup("/api/quotes") means every route below is automatically
        // prefixed with /api/quotes (so "/" really means GET /api/quotes).
        //
        // .RequireAuthorization() here still means "must be authenticated
        // at all" — a baseline every route needs. Each route below then
        // adds its own, more specific, scope policy on top of that
        // baseline, because "logged in" and "allowed to write" are
        // different questions with different answers per endpoint.
        var group = app.MapGroup("/api/quotes")
            .RequireAuthorization();

        // GET /api/quotes?page=1&size=10 — list quotes, paged.
        // Needs the "quotes.read" scope.
        group.MapGet("/", async (
            int page,
            int size,
            IQuoteRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (page < 1 || size < 1 || size > 100)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["page"] = new[] { "Page must be at least 1." },
                    ["size"] = new[] { "Size must be between 1 and 100." }
                });
            }

            var (items, total) = await repository.GetPagedAsync(
                page,
                size,
                cancellationToken);

            return Results.Ok(new
            {
                page,
                size,
                total,
                items
            });
        }).RequireAuthorization("can-read-quotes");

        // POST /api/quotes — create a new quote.
        // Needs the "quotes.write" scope. The creator's own user id is
        // stamped onto the new quote (CreatedByUserId), so a later delete
        // attempt can be checked against it — see MustOwnQuoteHandler.
        group.MapPost("/", async (
            CreateQuoteRequest request,
            IQuoteRepository repository,
            IQuoteTextNormalizer normalizer,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            var errors = new Dictionary<string, string[]>();

            if (string.IsNullOrWhiteSpace(request.Author))
                errors["author"] = new[] { "Author is required." };

            if (string.IsNullOrWhiteSpace(request.Text))
                errors["text"] = new[] { "Text is required." };

            if (request.Author?.Length > 200)
                errors["author"] = new[] { "Author must be 200 characters or less." };

            if (request.Text?.Length > 1000)
                errors["text"] = new[] { "Text must be 1000 characters or less." };

            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            // "sub" is the standard JWT claim for "who is this token
            // about." Depending on how the token was validated it can show
            // up as the long ClaimTypes.NameIdentifier claim type or still
            // as the raw "sub" name, so both are checked rather than
            // assuming one.
            var callerId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("sub")?.Value;

            // Quote.Create re-checks these same rules -- redundant here
            // since the validation above already guarantees non-null,
            // in-range values, but it means Quote.Create's own invariants
            // can never silently drift out of sync with what this endpoint
            // enforces, and any OTHER caller that skips this endpoint still
            // gets the same guarantees for free.
            var quote = Quote.Create(
                normalizer.Normalize(request.Author!),
                normalizer.Normalize(request.Text!),
                callerId);

            var created = await repository.AddAsync(
                quote,
                cancellationToken);

            return Results.Created(
                $"/api/quotes/{created.Id}",
                created);
        }).RequireAuthorization("can-edit-quotes");

        // GET /api/quotes/{id} — fetch a single quote.
        // Reading one quote needs the same permission as listing them.
        group.MapGet("/{id:int}", async (
            int id,
            IQuoteRepository repository,
            CancellationToken cancellationToken) =>
        {
            var quote = await repository.GetByIdAsync(
                id,
                cancellationToken);

            return quote is null
                ? Results.NotFound()
                : Results.Ok(quote);
        }).RequireAuthorization("can-read-quotes");

        // DELETE /api/quotes/{id} — remove a quote.
        // Two separate checks apply here, and they run in a deliberate
        // order:
        //   1. "can-delete-quotes" is a route-level policy — it runs
        //      BEFORE this delegate's body, purely from the caller's
        //      token. It rejects (403) anyone who doesn't carry the
        //      quotes.delete scope at all, before the database is touched.
        //   2. Ownership is checked HERE, imperatively, only after the
        //      quote has been loaded — because "do you own THIS quote"
        //      can't be known from the token alone. A caller can pass
        //      check #1 (they have delete permission in general) and still
        //      be blocked by check #2 for a quote that isn't theirs.
        group.MapDelete("/{id:int}", async (
            int id,
            IQuoteRepository repository,
            IAuthorizationService authorizationService,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            var quote = await repository.GetByIdAsync(id, cancellationToken);

            if (quote is null)
                return Results.NotFound();

            var ownershipResult = await authorizationService.AuthorizeAsync(
                user, quote, new MustOwnQuoteRequirement());

            if (!ownershipResult.Succeeded)
                return Results.Forbid();

            var deleted = await repository.DeleteAsync(
                id,
                cancellationToken);

            return deleted
                ? Results.NoContent()
                : Results.NotFound();
        }).RequireAuthorization("can-delete-quotes");

        return app;
    }
}

/// <summary>Shape of the JSON body for POST /api/quotes.</summary>
public record CreateQuoteRequest(string? Author, string? Text);
