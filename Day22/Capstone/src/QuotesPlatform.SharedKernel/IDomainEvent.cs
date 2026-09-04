namespace QuotesPlatform.SharedKernel;

/// <summary>
/// Something that happened inside one module, in the past tense.
///
/// NOT the same thing as an integration event in QuotesPlatform.Contracts. A
/// domain event is internal to a module and may carry entities; an integration
/// event crosses a boundary, is part of another module's contract, and may
/// therefore carry only ids and primitives. Conflating them is how a module's
/// internal model becomes another module's compile-time dependency.
/// </summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
