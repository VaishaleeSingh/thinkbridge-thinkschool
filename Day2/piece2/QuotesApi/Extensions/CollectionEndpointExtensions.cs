using System.Security.Claims;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;

namespace QuotesApi.Extensions;

/// <summary>
/// All HTTP endpoints for /api/collections live here.
///
/// Before this change, this whole group had NO authorization at all --
/// any anonymous request could create, read, or mutate a collection. That
/// gap is closed the same way /api/quotes was closed in
/// QuoteEndpointExtensions.cs: a baseline .RequireAuthorization() on the
/// group, plus a specific collections.* scope policy on every route --
/// see InfrastructureExtensions.cs for what each policy checks.
/// </summary>
public static class CollectionEndpointExtensions
{
    public static IEndpointRouteBuilder MapCollectionEndpoints(
        this IEndpointRouteBuilder app)
    {
        // .RequireAuthorization() here means "must be authenticated at
        // all" -- a baseline every route needs. Each route below then adds
        // its own, more specific, scope policy on top of that baseline.
        var group = app.MapGroup("/api/collections")
            .RequireAuthorization();

        // Create a collection. Validation (name length, non-empty) lives
        // on the aggregate's constructor, not here -- a broken invariant
        // throws and the middleware turns it into a 400 ProblemDetails.
        //
        // OwnerId is taken from the CALLER's own token, not from the
        // request body. Trusting a client-supplied "ownerId" would let
        // any authenticated caller create a collection claiming to belong
        // to someone else entirely -- the same reasoning
        // QuoteEndpointExtensions already uses for CreatedByUserId.
        group.MapPost("/", async (
            CreateCollectionRequest request,
            ICollectionRepository repository,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            var ownerId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("sub")?.Value
                ?? throw new InvalidOperationException(
                    "Authenticated request had no caller id claim.");

            var collection = new Collection(request.Name, ownerId);

            await repository.AddAsync(collection, cancellationToken);

            return Results.Created(
                $"/api/collections/{collection.Id}",
                collection);
        }).RequireAuthorization("can-edit-collections");

        // GET /api/collections/{id} -- needs the collections.read scope.
        group.MapGet("/{id:int}", async (
            int id,
            ICollectionRepository repository,
            CancellationToken cancellationToken) =>
        {
            var collection = await repository.GetByIdAsync(id, cancellationToken);

            return collection is null
                ? Results.NotFound()
                : Results.Ok(collection);
        }).RequireAuthorization("can-read-collections");

        // Add a quote to a collection. All mutation goes through the
        // aggregate root: collection.AddItem(...) enforces the max-50
        // and no-duplicate-QuoteId invariants and throws when they'd
        // break, rather than the endpoint touching db.Items directly.
        // Needs the collections.write scope.
        group.MapPost("/{id:int}/items", async (
            int id,
            AddCollectionItemRequest request,
            ICollectionRepository repository,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var collection = await repository.GetByIdAsync(id, cancellationToken);

            if (collection is null)
                return Results.NotFound();

            collection.AddItem(request.QuoteId, clock.UtcNow);

            await repository.UpdateAsync(collection, cancellationToken);

            return Results.Ok(collection);
        }).RequireAuthorization("can-edit-collections");

        // Remove a quote from a collection. collection.RemoveItem(...)
        // throws KeyNotFoundException (-> 404 ProblemDetails) if the
        // quote isn't in the collection. Needs the collections.delete
        // scope.
        group.MapDelete("/{id:int}/items/{quoteId:int}", async (
            int id,
            int quoteId,
            ICollectionRepository repository,
            CancellationToken cancellationToken) =>
        {
            var collection = await repository.GetByIdAsync(id, cancellationToken);

            if (collection is null)
                return Results.NotFound();

            collection.RemoveItem(quoteId);

            await repository.UpdateAsync(collection, cancellationToken);

            return Results.NoContent();
        }).RequireAuthorization("can-delete-collections");

        return app;
    }
}

/// <summary>
/// Shape of the JSON body for POST /api/collections. Note there is no
/// OwnerId here anymore -- it used to be taken from this request body,
/// which meant any caller could claim to own a collection as anyone.
/// OwnerId is now always derived from the caller's authenticated identity
/// instead (see the endpoint above).
/// </summary>
public record CreateCollectionRequest(string Name);

public record AddCollectionItemRequest(int QuoteId);
