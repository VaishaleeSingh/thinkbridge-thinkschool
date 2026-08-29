namespace QuotesBff;

/// <summary>
/// Everything this proxy needs to know, and deliberately nothing that is a
/// secret.
///
/// <para>
/// That is the property worth stating out loud: an app setting on this Function
/// App is readable by anyone with Reader on the resource, and this exercise's
/// whole point is that the credential for the Week-1 API is not one of them.
/// A base URL and an audience are both public facts -- the audience is already
/// committed in <c>Day7/piece2/QuotesApi/appsettings.json</c>. The credential
/// comes from the platform's identity endpoint at runtime and is never at rest.
/// </para>
/// </summary>
public sealed class BffOptions
{
    public const string SectionName = "Api";

    /// <summary>Origin of the Week-1 QuotesApi on Azure Container Apps. No trailing slash.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// The API's Entra application ID URI (<c>api://quotes-api/access</c>).
    /// <c>/.default</c> is appended when requesting the token: that suffix is
    /// what asks for an app-only (client-credentials) token carrying whatever
    /// app roles this identity has been granted, rather than a delegated scope.
    /// </summary>
    public string Audience { get; init; } = string.Empty;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            throw new InvalidOperationException("Api__BaseUrl is not configured.");
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"Api__BaseUrl must be an absolute https URL. Got: '{BaseUrl}'.");
        }

        if (string.IsNullOrWhiteSpace(Audience))
        {
            throw new InvalidOperationException("Api__Audience is not configured.");
        }
    }
}
