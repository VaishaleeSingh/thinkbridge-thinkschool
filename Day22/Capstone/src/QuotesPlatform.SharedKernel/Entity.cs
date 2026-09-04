namespace QuotesPlatform.SharedKernel;

/// <summary>
/// Identity is the id, not the reference. Two instances loaded in different
/// scopes with the same id are the same entity.
/// </summary>
public abstract class Entity<TId> where TId : notnull
{
    public TId Id { get; protected set; } = default!;

    public override bool Equals(object? obj) =>
        obj is Entity<TId> other
        && other.GetType() == GetType()
        && EqualityComparer<TId>.Default.Equals(other.Id, Id);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
