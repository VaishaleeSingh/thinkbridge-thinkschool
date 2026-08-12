using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;

namespace QuotesApi.Extensions;

public static class CollectionEndpointExtensions
{
    public static IEndpointRouteBuilder MapCollectionEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/collections");

        // Create a collection. Validation (name length, non-empty) lives
        // on the aggregate's constructor, not here — a broken invariant
        // throws and the middleware turns it into a 400 ProblemDetails.
        group.MapPost("/", async (
            CreateCollectionRequest request,
            ICollectionRepository repository,
            CancellationToken cancellationToken) =>
        {
            var collection = new Collection(request.Name, request.OwnerId);

            await repository.AddAsync(collection, cancellationToken);

            return Results.Created(
                $"/api/collections/{collection.Id}",
                collection);
        });

        group.MapGet("/{id:int}", async (
            int id,
            ICollectionRepository repository,
            CancellationToken cancellationToken) =>
        {
            var collection = await repository.GetByIdAsync(id, cancellationToken);

            return collection is null
                ? Results.NotFound()
                : Results.Ok(collection);
        });

        // Add a quote to a collection. All mutation goes through the
        // aggregate root: collection.AddItem(...) enforces the max-50
        // and no-duplicate-QuoteId invariants and throws when they'd
        // break, rather than the endpoint touching db.Items directly.
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
        });

        // Remove a quote from a collection. collection.RemoveItem(...)
        // throws KeyNotFoundException (-> 404 ProblemDetails) if the
        // quote isn't in the collection.
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
        });

        return app;
    }
}

public record CreateCollectionRequest(string Name, string OwnerId);

public record AddCollectionItemRequest(int QuoteId);
