using Microsoft.AspNetCore.Mvc;
using QuotesApi.Models;
using QuotesApi.Repositories;

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

            var quote = new Quote
            {
                Author = request.Author.Trim(),
                Text = request.Text.Trim()
            };

            var created = await repository.AddAsync(
                quote,
                cancellationToken);

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