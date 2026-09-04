namespace QuotesPlatform.SharedKernel;

/// <summary>
/// The consistency boundary and the transaction boundary: one aggregate is
/// saved per transaction, and nothing outside it holds a reference to anything
/// inside it.
///
/// Domain events are RAISED here and DISPATCHED by infrastructure after the
/// transaction commits. That split is the whole point -- Day 20 exists because
/// a message published before the commit is a message about something that may
/// never have happened.
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId> where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}
