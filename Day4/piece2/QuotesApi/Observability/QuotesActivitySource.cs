using System.Diagnostics;

namespace QuotesApi.Observability;

/// <summary>
/// The single ActivitySource this app creates its own spans from.
///
/// Automatic instrumentation (AspNetCore, EntityFrameworkCore, HttpClient)
/// covers incoming requests, database queries and outbound HTTP calls. Work
/// that is none of those -- CPU-bound work in particular -- is invisible to
/// it, and shows up in a trace only as an unexplained gap between spans.
/// Those are what this source is for.
///
/// The name matters: spans from a source that was not registered with
/// .AddSource(...) in the tracing configuration are silently dropped, with
/// no error anywhere. Hence the const, referenced from both here and
/// ObservabilityExtensions, rather than the same string typed twice.
/// </summary>
public static class QuotesActivitySource
{
    public const string Name = "QuotesApi";

    public static readonly ActivitySource Instance = new(Name);
}
