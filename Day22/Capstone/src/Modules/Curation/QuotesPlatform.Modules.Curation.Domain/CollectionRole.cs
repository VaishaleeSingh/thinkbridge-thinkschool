namespace QuotesPlatform.Modules.Curation.Domain;

/// <summary>
/// Roles live in the aggregate because they are a rule about the collection,
/// not a rule about the request. An authorization attribute on an endpoint
/// cannot express "only the owner of THIS collection may submit it", and a
/// check written in a handler is a check the next handler forgets.
/// </summary>
public enum CollectionRole
{
    Contributor = 0,
    Owner = 1
}
