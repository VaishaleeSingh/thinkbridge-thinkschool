using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Extensions;
using QuotesApi.Middleware;
using Serilog;

// This file is the app's entry point. It runs top-to-bottom, once, when the
// app starts. Its whole job is to wire pieces together and then start
// listening for HTTP requests -- it deliberately contains no business logic.

var builder = WebApplication.CreateBuilder(args);

// Replaces the default Microsoft.Extensions.Logging console provider with
// Serilog end-to-end, reading levels/sinks from the "Serilog" config section
// (see appsettings.json) rather than "Logging" -- that section is what
// ReadFrom.Configuration actually looks for.
//
// This uses the (context, services, loggerConfig) lambda form rather than
// the more common `Log.Logger = new LoggerConfiguration()...CreateLogger();`
// static-assignment pattern on purpose: this exact Program.cs is re-run
// fresh inside every WebApplicationFactory<Program> in the integration test
// suite, often several at once. A single shared static Log.Logger getting
// reassigned/disposed across concurrently-running test hosts would be
// exactly the kind of hidden cross-test coupling worth avoiding. Scoping
// the logger pipeline to each host's own builder keeps every test's
// Serilog setup fully independent, with no global mutable state at all.
builder.Host.UseSerilog((context, services, loggerConfig) =>
    loggerConfig
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext());

// AddProblemDetails() makes unhandled errors come back to the client as a
// standard RFC 7807 JSON shape instead of a raw stack trace.
builder.Services.AddProblemDetails();

// Everything the app needs (database, repositories, services, and
// authentication/authorization) is registered inside this one method. See
// Extensions/InfrastructureExtensions.cs for the details of each piece.
builder.Services.AddInfrastructure(builder.Configuration);

// Distributed tracing (spans for requests, EF queries, outbound HTTP, plus
// this app's own custom spans). See ObservabilityExtensions.cs -- in
// particular for why the OTLP exporter is only wired up when an endpoint is
// actually configured.
builder.Services.AddObservability(builder.Configuration);

var app = builder.Build();

// Stamps every log line written during a request with that request's
// TraceId (see CorrelationIdMiddleware). Registered FIRST, so it wraps
// ExceptionHandlingMiddleware below rather than sitting after it: the one
// log line you most need tied back to a request is the exception log for a
// failed one, and that has to carry the same TraceId as everything else
// from that request.
app.UseMiddleware<CorrelationIdMiddleware>();

// Catches any exception that escapes an endpoint and turns it into a clean
// ProblemDetails response instead of leaking a raw .NET stack trace.
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Applies any pending EF Core migrations on startup, so the database schema
// is always up to date before the app starts accepting requests.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
    await db.Database.MigrateAsync();
}

// --- Turn authentication/authorization ON ----------------------------------
// UseAuthentication() reads the incoming request's token and figures out
// "who is this?" (it runs the CustomJwt/EntraId/MultiScheme logic that was
// registered in InfrastructureExtensions.cs).
//
// UseAuthorization() then enforces "are they allowed to call this endpoint?"
// on any endpoint marked with .RequireAuthorization() (see
// QuoteEndpointExtensions.cs and CollectionEndpointExtensions.cs).
//
// Order matters: authentication must run before authorization, and both
// must run before the endpoints below so the identity is known by the time
// a request reaches them.
app.UseAuthentication();
app.UseAuthorization();

// /api/auth/* is intentionally mapped without any auth requirement of its
// own -- these are the endpoints that HAND OUT tokens in the first place.
app.MapAuthEndpoints();

app.MapQuoteEndpoints();
app.MapCollectionEndpoints();

app.Run();

// Exposes the auto-generated Program class to the test project, so
// WebApplicationFactory<Program> in the integration tests can boot this
// exact app in-memory.
public partial class Program { }
