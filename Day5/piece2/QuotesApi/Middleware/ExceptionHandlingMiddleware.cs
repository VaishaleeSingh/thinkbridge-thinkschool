using Microsoft.AspNetCore.Mvc;

namespace QuotesApi.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            var problem = MapToProblemDetails(ex);

            var logLevel = problem.Status >= 500
                ? LogLevel.Error
                : LogLevel.Warning;

            _logger.Log(
                logLevel,
                ex,
                "Exception while processing {Path}: {Message}",
                context.Request.Path,
                ex.Message);

            context.Response.StatusCode = problem.Status!.Value;
            context.Response.ContentType = "application/problem+json";

            await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
        }
    }

    private static ProblemDetails MapToProblemDetails(Exception ex)
    {
        // ArgumentException/InvalidOperationException are how the domain
        // aggregates (e.g. Collection) signal broken invariants, so they
        // map to 400 rather than bubbling up as a generic 500.
        return ex switch
        {
            // A malformed or unreadable request body is the CLIENT's mistake,
            // and BadHttpRequestException already carries the right status
            // code (400) -- it was previously falling through to the
            // catch-all below and being reported as a 500. That matters for
            // more than tidiness: a 500 says "this server is broken", so a
            // typo in someone's JSON would page whoever is on call and count
            // against the service's error budget, while telling the caller
            // nothing they can act on.
            //
            // The Detail is deliberately generic rather than echoing
            // ex.Message: this handler also covers the login endpoint, whose
            // request body contains a password, and parse-error text is not
            // somewhere to risk leaking any of it.
            BadHttpRequestException badRequest => new ProblemDetails
            {
                Status = badRequest.StatusCode,
                Title = "The request could not be read.",
                Detail = "The request body could not be parsed. Check that it is valid JSON matching the expected shape."
            },
            ArgumentException => new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "One or more invariants were violated.",
                Detail = ex.Message
            },
            InvalidOperationException => new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "One or more invariants were violated.",
                Detail = ex.Message
            },
            KeyNotFoundException => new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Resource not found.",
                Detail = ex.Message
            },
            _ => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Detail = "The server encountered an unexpected error."
            }
        };
    }
}