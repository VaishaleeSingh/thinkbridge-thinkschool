using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QuotesApi.Authorization;
using QuotesApi.Messaging;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using QuotesApi.Configuration;

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
            // IOptionsSnapshot, not IOptions, and here that IS the right
            // choice -- unlike JwtOptions (see AuthService for why that one
            // must stay fixed for the process lifetime). A page-size
            // ceiling is an operational dial: if a client starts asking for
            // huge pages and the database feels it, someone should be able
            // to lower the limit in configuration and have the very next
            // request respect it, with no redeploy and no restart. Nothing
            // else in the app has to agree with this value, so re-reading
            // it per request cannot cause anything to drift out of step.
            IOptionsSnapshot<PaginationOptions> paginationOptions,
            CancellationToken cancellationToken) =>
        {
            var maxPageSize = paginationOptions.Value.MaxPageSize;

            if (page < 1 || size < 1 || size > maxPageSize)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["page"] = new[] { "Page must be at least 1." },
                    ["size"] = new[] { $"Size must be between 1 and {maxPageSize}." }
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
            IQuoteEventPublisher publisher,
            IClock clock,
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

            if (!string.IsNullOrWhiteSpace(request.BackgroundImageUrl))
            {
                try
                {
                    _ = Quote.ResolveBackgroundImageUrl(request.BackgroundImageUrl);
                }
                catch (ArgumentException exception)
                {
                    errors["backgroundImageUrl"] = new[] { exception.Message };
                }
            }

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
                callerId,
                request.BackgroundImageUrl);

            var created = await repository.AddAsync(
                quote,
                cancellationToken);

            // Publish AFTER commit. Not atomic with the database write:
            // a crash here loses the event. See submission notes for why
            // the transactional outbox is the correct fix.
            //
            // CancellationToken.None deliberately, NOT the request token:
            // the write has already committed, so a client that disconnects
            // (or a browser that cancels the request) must not also cancel
            // the publish and silently drop an event describing a change
            // that is durably in the database.
            var evt = QuoteChangedEvent.Created(
                created.Id, callerId, created.Author, created.Text, clock.UtcNow);
            await publisher.PublishAsync(evt, CancellationToken.None);

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

        // PUT /api/quotes/{id} -- update an existing quote.
        // Needs write permission and ownership checks, mirroring delete.
        group.MapPut("/{id:int}", async (
            int id,
            UpdateQuoteRequest request,
            IQuoteRepository repository,
            IQuoteTextNormalizer normalizer,
            IQuoteEventPublisher publisher,
            IClock clock,
            IAuthorizationService authorizationService,
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

            if (!string.IsNullOrWhiteSpace(request.BackgroundImageUrl))
            {
                try
                {
                    _ = Quote.ResolveBackgroundImageUrl(request.BackgroundImageUrl);
                }
                catch (ArgumentException exception)
                {
                    errors["backgroundImageUrl"] = new[] { exception.Message };
                }
            }

            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var quote = await repository.GetByIdAsync(id, cancellationToken);

            if (quote is null)
                return Results.NotFound();

            var ownershipResult = await authorizationService.AuthorizeAsync(
                user, quote, new MustOwnQuoteRequirement());

            if (!ownershipResult.Succeeded)
                return Results.Forbid();

            var normalizedAuthor = normalizer.Normalize(request.Author!);
            var normalizedText = normalizer.Normalize(request.Text!);
            var normalizedBackground = Quote.ResolveBackgroundImageUrl(
                request.BackgroundImageUrl,
                $"{normalizedAuthor}|{normalizedText}");

            var updated = await repository.UpdateAsync(
                id,
                normalizedAuthor,
                normalizedText,
                normalizedBackground,
                cancellationToken);

            if (updated is null)
                return Results.NotFound();

            var callerId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("sub")?.Value;
            // CancellationToken.None: see the POST handler above -- the
            // write is committed, the publish must not be cancelled with the
            // request.
            var evt = QuoteChangedEvent.Updated(
                updated.Id, callerId, updated.Author, updated.Text, clock.UtcNow);
            await publisher.PublishAsync(evt, CancellationToken.None);

            return Results.Ok(updated);
        }).RequireAuthorization("can-edit-quotes");

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
            IQuoteEventPublisher publisher,
            IClock clock,
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

            if (!deleted)
                return Results.NotFound();

            var callerId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("sub")?.Value;
            // CancellationToken.None: see the POST handler above.
            var evt = QuoteChangedEvent.Deleted(id, callerId, clock.UtcNow);
            await publisher.PublishAsync(evt, CancellationToken.None);

            return Results.NoContent();
        }).RequireAuthorization("can-delete-quotes");

        return app;
    }
}

/// <summary>Shape of the JSON body for POST /api/quotes.</summary>
public record CreateQuoteRequest(string? Author, string? Text, string? BackgroundImageUrl = null);

/// <summary>Shape of the JSON body for PUT /api/quotes/{id}.</summary>
public record UpdateQuoteRequest(string? Author, string? Text, string? BackgroundImageUrl = null);
