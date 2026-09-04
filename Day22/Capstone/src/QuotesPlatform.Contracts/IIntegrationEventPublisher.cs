namespace QuotesPlatform.Contracts;

/// <summary>
/// How a module hands a cross-boundary fact to the outbox.
///
/// A JUDGEMENT CALL, since this project is otherwise records only: the
/// alternative is an identical interface in every module's Application
/// project, and four copies of one abstraction is how two of them drift. The
/// port lives with the contracts it carries because it IS part of the
/// cross-module contract -- unlike a repository port, which is private to its
/// module and stays there.
///
/// Implemented in the Host's shared infrastructure by the transactional outbox
/// from Day 20: the write and the message commit together or not at all.
/// </summary>
public interface IIntegrationEventPublisher
{
    /// <summary>
    /// Enqueues the event in the same transaction as the caller's work. It is
    /// NOT sent here -- the relay picks it up after the commit, which is the
    /// entire point of an outbox.
    /// </summary>
    Task EnqueueAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default);
}
