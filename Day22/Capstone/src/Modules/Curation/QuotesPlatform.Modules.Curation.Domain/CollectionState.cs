namespace QuotesPlatform.Modules.Curation.Domain;

/// <summary>
/// Draft -> InReview -> Published, with Revising as the loop back for an
/// already-published collection.
///
/// Revising exists so that editing a published collection never takes the live
/// edition away from readers: the edition keeps serving while the next one is
/// being worked on. Without it, "edit" would have to mean either "unpublish
/// first" or "mutate what readers are looking at", and both are worse.
/// </summary>
public enum CollectionState
{
    Draft = 0,
    InReview = 1,
    Published = 2,
    Revising = 3,
    Archived = 4
}
