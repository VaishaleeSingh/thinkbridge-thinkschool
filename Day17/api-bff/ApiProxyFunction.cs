using System.Net;
using System.Text;
using System.Text.Json;

using Azure.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace QuotesBff;

/// <summary>
/// The one place where a request from the browser becomes a request to the
/// Week-1 API, and the one place a token is attached.
///
/// <para>
/// WHY ONE CATCH-ALL AND NOT A FUNCTION PER ENDPOINT: the security property this
/// whole exercise is about -- every call to the API carries a managed-identity
/// token and no secret exists -- is only true if there is nowhere else for a
/// call to come from. Eleven functions means eleven places to forget the token
/// when a twelfth endpoint is added. One means the property holds by
/// construction.
/// </para>
///
/// <para>
/// TWO CREDENTIALS, TWO JOBS. This is the part to understand before changing
/// anything here:
/// </para>
/// <list type="bullet">
///   <item>
///     The <b>managed-identity token</b> in <c>Authorization</c> authenticates
///     the calling <i>application</i>. It is app-only: it carries an app role
///     (<c>Quotes.Proxy</c>) and no user at all. It is what makes the claim
///     "the call carries a managed-identity token" true.
///   </item>
///   <item>
///     The <b>user's first-party JWT</b> is forwarded in
///     <c>X-Forwarded-Authorization</c>. It has to be: Day 3 built
///     resource-based ownership checks on <c>/api/collections</c>, and those
///     need to know which user is asking. Replacing the user's token with the
///     managed-identity token would silently make every collection readable by
///     everyone -- a security regression dressed up as a deployment.
///   </item>
/// </list>
///
/// <para>
/// The API must ignore <c>X-Forwarded-Authorization</c> unless the request also
/// carried a valid app-only token with the expected role and caller object id.
/// A forwarded-identity header is only safe when the transport is itself
/// authenticated; here the managed-identity token is what authenticates it. If
/// that gate is ever removed on the API side, this header becomes a free
/// impersonation primitive.
/// </para>
/// </summary>
public sealed class ApiProxyFunction
{
    public const string HttpClientName = "quotes-api";

    /// <summary>
    /// The only path prefixes that may be proxied.
    ///
    /// <para>
    /// The Week-1 API also exposes <c>/api/diagnostics/*</c> -- the Day 5 N+1
    /// reproduction endpoints and a <c>POST /seed</c>. Those are development
    /// tooling and must not be reachable from the internet. An allowlist rather
    /// than a denylist, so an endpoint group added to the API later is
    /// unreachable until someone deliberately adds it here.
    /// </para>
    /// </summary>
    private static readonly string[] AllowedPrefixes = ["auth", "quotes", "collections"];

    /// <summary>
    /// Headers that must never be copied in either direction. <c>Authorization</c>
    /// because this class decides what it is; the hop-by-hop and content-framing
    /// headers because <c>HttpClient</c> and the Functions host each set their own
    /// and copying them produces a response the runtime rejects.
    /// </summary>
    private static readonly HashSet<string> HeadersNotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Host", "Connection", "Keep-Alive", "Transfer-Encoding",
        "Upgrade", "Proxy-Authorization", "TE", "Trailer", "Content-Length",
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TokenCredential _credential;
    private readonly BffOptions _options;
    private readonly ILogger<ApiProxyFunction> _logger;

    public ApiProxyFunction(
        IHttpClientFactory httpClientFactory,
        TokenCredential credential,
        IOptions<BffOptions> options,
        ILogger<ApiProxyFunction> logger)
    {
        _httpClientFactory = httpClientFactory;
        _credential = credential;
        _options = options.Value;
        _logger = logger;
    }

    [Function("ApiProxy")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "get", "post", "put", "patch", "delete",
            Route = "{*path}")]
        HttpRequestData request,
        string path,
        CancellationToken cancellationToken)
    {
        // AuthorizationLevel.Anonymous is correct and is not an oversight. The
        // caller here is the browser, which holds no function key -- it holds a
        // user JWT that the *API* validates. A function key would be a shared
        // secret shipped to every browser, which is the thing this design
        // exists to avoid. The linked-backend relationship is what stops this
        // Function App being callable from anywhere except the static web app.
        if (!IsAllowed(path))
        {
            _logger.LogWarning("Rejected proxy request for disallowed path {Path}.", path);
            return request.CreateResponse(HttpStatusCode.NotFound);
        }

        AccessToken token;
        try
        {
            // "/.default" asks for an app-only token for this resource carrying
            // whatever app roles this managed identity has been granted. This is
            // the single line the exercise is really about: no client id, no
            // secret, no certificate -- the platform vouches for the caller.
            token = await _credential.GetTokenAsync(
                new TokenRequestContext([$"{_options.Audience.TrimEnd('/')}/.default"]),
                cancellationToken);
        }
        catch (Exception ex)
        {
            // Deliberately distinguishable from a downstream failure. A failure
            // here means the identity is not assigned, or the app role is not
            // granted, or IMDS is unreachable -- none of which are the API's
            // fault, and all of which look identical in a generic 502.
            _logger.LogError(ex, "Could not acquire a managed-identity token for {Audience}.", _options.Audience);
            return request.CreateResponse(HttpStatusCode.BadGateway);
        }

        LogTokenShape(token);

        using var outbound = BuildOutboundRequest(request, path, token);

        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var upstream = await client.SendAsync(
            outbound, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        return await CopyBackAsync(request, upstream, cancellationToken);
    }

    private static bool IsAllowed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // Reject traversal before comparing prefixes: "quotes/../diagnostics/seed"
        // starts with an allowed prefix and is not an allowed path.
        if (path.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var firstSegment = path.Split('/', 2)[0];

        return AllowedPrefixes.Contains(firstSegment, StringComparer.OrdinalIgnoreCase);
    }

    private HttpRequestMessage BuildOutboundRequest(HttpRequestData request, string path, AccessToken token)
    {
        var query = request.Url.Query;   // already percent-encoded; passed through verbatim
        var outbound = new HttpRequestMessage(
            new HttpMethod(request.Method),
            $"api/{path}{query}");

        outbound.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

        // The user's identity, forwarded under a header the API only honours on
        // an app-only-authenticated request. See the class remarks.
        if (request.Headers.TryGetValues("Authorization", out var inbound))
        {
            var userToken = inbound.FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(userToken))
            {
                outbound.Headers.TryAddWithoutValidation("X-Forwarded-Authorization", userToken);
            }
        }

        // Correlation id, so one request can be followed from the browser through
        // this proxy into the API's Serilog output (Day 4 wired the API half).
        var correlationId = request.Headers.TryGetValues("X-Correlation-Id", out var existing)
            ? existing.FirstOrDefault()
            : null;

        outbound.Headers.TryAddWithoutValidation(
            "X-Correlation-Id",
            string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("n") : correlationId);

        foreach (var header in request.Headers)
        {
            if (HeadersNotForwarded.Contains(header.Key))
            {
                continue;
            }

            outbound.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Body is { Length: > 0 })
        {
            var content = new StreamContent(request.Body);

            if (request.Headers.TryGetValues("Content-Type", out var contentType))
            {
                content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }

            outbound.Content = content;
        }

        return outbound;
    }

    private static async Task<HttpResponseData> CopyBackAsync(
        HttpRequestData request, HttpResponseMessage upstream, CancellationToken cancellationToken)
    {
        var response = request.CreateResponse(upstream.StatusCode);

        foreach (var header in upstream.Headers.Concat(upstream.Content.Headers))
        {
            if (HeadersNotForwarded.Contains(header.Key))
            {
                continue;
            }

            response.Headers.TryAddWithoutValidation(header.Key, string.Join(", ", header.Value));
        }

        await upstream.Content.CopyToAsync(response.Body, cancellationToken);

        return response;
    }

    /// <summary>
    /// Logs the token's <i>shape</i> -- issuer, audience, roles, object id,
    /// expiry -- and never the signature.
    ///
    /// <para>
    /// This exists specifically to make the exercise's central claim checkable
    /// rather than asserted. What proves the call is authorised by a managed
    /// identity and not by a user or a secret is the combination:
    /// <c>roles</c> present, <c>scp</c> and <c>upn</c> absent, and <c>oid</c>
    /// equal to this Function App's <c>principalId</c>.
    /// </para>
    ///
    /// <para>
    /// Only the first two segments of the JWT are decoded. The third is the
    /// signature; logging it would put a usable credential in Application
    /// Insights, which is precisely the class of mistake this deployment is
    /// meant to avoid.
    /// </para>
    /// </summary>
    private void LogTokenShape(AccessToken token)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        try
        {
            var segments = token.Token.Split('.');

            if (segments.Length < 2)
            {
                return;
            }

            using var payload = JsonDocument.Parse(Base64UrlDecode(segments[1]));
            var claims = payload.RootElement;

            string? Claim(string name) =>
                claims.TryGetProperty(name, out var value) ? value.ToString() : null;

            _logger.LogInformation(
                "Managed-identity token acquired. iss={Issuer} aud={Audience} appid={AppId} oid={ObjectId} " +
                "roles={Roles} scp={Scope} upn={Upn} expires={ExpiresOn:O}",
                Claim("iss"), Claim("aud"), Claim("appid") ?? Claim("azp"), Claim("oid"),
                Claim("roles") ?? "(none)", Claim("scp") ?? "(none - app-only, as expected)",
                Claim("upn") ?? "(none - app-only, as expected)", token.ExpiresOn);
        }
        catch (Exception ex)
        {
            // Never fail a request because the diagnostic logging failed.
            _logger.LogDebug(ex, "Could not decode the token payload for logging.");
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
