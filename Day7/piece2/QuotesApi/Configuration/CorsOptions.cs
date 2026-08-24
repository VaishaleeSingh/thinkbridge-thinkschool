using System.ComponentModel.DataAnnotations;

namespace QuotesApi.Configuration;

/// <summary>
/// The browser origins allowed to call this API from JavaScript, bound from
/// the "Cors" configuration section.
///
/// WHY THIS TYPE EXISTS (Day 13):
/// until Day 13 every client of this API was a server, a CLI, or a test --
/// none of which the browser's same-origin policy applies to, so the API
/// never needed a CORS policy and deliberately did not have one. Day 13
/// adds an Angular SPA served from a different origin
/// (http://localhost:4200 in development, some other host once deployed),
/// and a browser will refuse to hand that page any response from this API
/// unless the API itself says the origin is allowed.
///
/// It is a configured list rather than a hardcoded one, and rather than
/// AllowAnyOrigin(), for two reasons. The dev origin and the deployed
/// origin are different values of the same setting, so neither belongs in
/// source. And AllowAnyOrigin() cannot be combined with credentials at all,
/// which would leave the API's real access rule ("any site may read this
/// with a token it somehow obtained") looser than anyone reading the code
/// would expect.
///
/// An empty list is a deliberate, safe default: no origin is allowed, which
/// is exactly the behaviour this API had before Day 13.
/// </summary>
public sealed class CorsOptions
{
    public const string SectionName = "Cors";

    /// <summary>
    /// Absolute origins -- scheme, host and port, no trailing slash, e.g.
    /// "http://localhost:4200". A path here silently never matches, so the
    /// format is validated rather than trusted.
    /// </summary>
    public string[] AllowedOrigins { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Returns the origins that are actually usable, and the ones that are
    /// not, so startup can log or fail on a malformed entry instead of
    /// leaving a developer staring at a CORS error whose real cause is a
    /// trailing slash in configuration.
    /// </summary>
    public (string[] Valid, string[] Invalid) Partition()
    {
        var valid = new List<string>();
        var invalid = new List<string>();

        foreach (var origin in AllowedOrigins)
        {
            if (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && uri.AbsolutePath == "/"
                && !origin.EndsWith('/'))
            {
                valid.Add(origin);
            }
            else
            {
                invalid.Add(origin);
            }
        }

        return (valid.ToArray(), invalid.ToArray());
    }
}
