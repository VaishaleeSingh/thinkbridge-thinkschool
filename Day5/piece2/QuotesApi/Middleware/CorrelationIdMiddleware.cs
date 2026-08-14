using System.Diagnostics;
using Serilog.Context;

namespace QuotesApi.Middleware;

/// <summary>
/// Stamps every log line written while handling a request with that
/// request's TraceIdentifier, under the property name "TraceId", so all the
/// lines belonging to one request can be pulled back together later by a
/// single correlation ID -- which is the entire point of structured logging
/// once more than one request is in flight at a time.
///
/// It works by pushing the property onto Serilog's ambient LogContext (an
/// AsyncLocal), which the "Enrich.FromLogContext()" enricher configured in
/// Program.cs then reads when it builds each LogEvent. Nothing downstream
/// has to know this middleware exists or pass a trace ID around by hand.
///
/// WHY THIS IS A CLASS AND NOT AN INLINE app.Use(...) LAMBDA:
/// it started as one. As a lambda buried in Program.cs, the only way to
/// exercise it was to boot the entire app through WebApplicationFactory and
/// then try to intercept Serilog's output from outside -- which meant the
/// test depended on host build ordering, on the test project's sink
/// assembly being resolvable from the app's logger configuration, and on
/// static singleton state shared across concurrently-running test hosts.
/// None of that has anything to do with the behaviour being verified.
/// Underneath, this is a small, pure piece of logic: given an HttpContext,
/// make TraceId visible to logging for the duration of the call. As a named
/// class it can be constructed directly with a fake "next" delegate and a
/// real Serilog logger, and every part of its behaviour -- the property is
/// attached, and it does not leak past the end of the request -- is
/// verifiable with no host, no HTTP, and no shared global state.
/// </summary>
public class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Prefer the ambient Activity's TraceId over HttpContext.TraceIdentifier.
        // Both are usually the same value once tracing is enabled, but only
        // the Activity one is GUARANTEED to be the W3C trace ID that
        // OpenTelemetry reports to the trace backend -- which is the whole
        // point of putting it on log lines. TraceIdentifier is an ASP.NET
        // Core implementation detail that has historically been a
        // connection-scoped "id:requestNumber" string instead, so relying
        // on it to match the trace would be relying on a coincidence.
        // It stays as the fallback for the case where no Activity is
        // running at all (tracing disabled, or code invoked outside a
        // request), where it is still better than nothing.
        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        // The "using" matters: it pops the property again once the request
        // finishes, so a TraceId can never bleed onto log lines belonging to
        // a later request that reuses this thread.
        using (LogContext.PushProperty("TraceId", traceId))
        {
            await _next(context);
        }
    }
}
