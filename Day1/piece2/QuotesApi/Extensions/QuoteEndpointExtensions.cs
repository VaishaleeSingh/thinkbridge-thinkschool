using Microsoft.AspNetCore.Mvc;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;

namespace QuotesApi.Extensions;

public static class QuoteEndpointExtensions
{
    public static IEndpointRouteBuilder MapQuoteEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/quotes");

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
        });

        group.MapPost("/", async (
            CreateQuoteRequest request,
            IQuoteRepository repository,
            IQuoteTextNormalizer normalizer,
            CancellationToken cancellationToken) =>
        {
            // Normalize input first
            var normalizedAuthor = normalizer.Normalize(request.Author ?? "");
            var normalizedText = normalizer.Normalize(request.Text ?? "");

            // Use the rich domain model's factory method to validate
            var (quote, error) = Quote.Create(normalizedAuthor, normalizedText);

            if (quote is null)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["quote"] = new[] { error! }
                });

            var created = await repository.AddAsync(quote, cancellationToken);

            return Results.Created(
                $"/api/quotes/{created.Id}",
                created);
        });

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
        });

        group.MapDelete("/{id:int}", async (
            int id,
            IQuoteRepository repository,
            CancellationToken cancellationToken) =>
        {
            var deleted = await repository.DeleteAsync(
                id,
                cancellationToken);

            return deleted
                ? Results.NoContent()
                : Results.NotFound();
        });

        return app;
    }
}

public record CreateQuoteRequest(string? Author, string? Text);