using Microsoft.AspNetCore.Mvc;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;

namespace QuotesApi.Extensions;

/// <summary>
/// All HTTP endpoints for /api/quotes live here. Each endpoint is a small
/// function: read the request, ask a repository/service to do the real
/// work, and translate the result into an HTTP response. There is no
/// database or business logic directly in this file — that lives in
/// QuoteRepository and friends.
/// </summary>
public static class QuoteEndpointExtensions
{
    public static IEndpointRouteBuilder MapQuoteEndpoints(
        this IEndpointRouteBuilder app)
    {
        // MapGroup("/api/quotes") means every route below is automatically
        // prefixed with /api/quotes (so "/" really means GET /api/quotes).
        //
        // .RequireAuthorization() (added Day 3) means every endpoint in this
        // group needs a valid token — either our own custom JWT or an Entra
        // ID token; see InfrastructureExtensions.cs for how that's decided.
        // Without this line, a request with no token at all would still
        // succeed, because authentication only checks a token IF one is
        // present — it doesn't demand one unless something tells it to.
        var group = app.MapGroup("/api/quotes")
            .RequireAuthorization();

        // GET /api/quotes?page=1&size=10 — list quotes, paged.
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

        // POST /api/quotes — create a new quote.
        group.MapPost("/", async (
            CreateQuoteRequest request,
            IQuoteRepository repository,
            IQuoteTextNormalizer normalizer,
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
                Author = normalizer.Normalize(request.Author),
                Text = normalizer.Normalize(request.Text)
            };

            var created = await repository.AddAsync(
                quote,
                cancellationToken);

            return Results.Created(
                $"/api/quotes/{created.Id}",
                created);
        });

        // GET /api/quotes/{id} — fetch a single quote.
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

        // DELETE /api/quotes/{id} — remove a quote.
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

/// <summary>Shape of the JSON body for POST /api/quotes.</summary>
public record CreateQuoteRequest(string? Author, string? Text);
