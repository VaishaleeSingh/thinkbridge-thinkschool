using QuotesPlatform.SharedKernel;

namespace QuotesPlatform.Modules.Moderation.Domain;

public enum ReviewSubject
{
    Quote = 0,
    Collection = 1
}

public enum ReviewOutcome
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}

/// <summary>
/// A review is its own aggregate rather than a field on the thing being
/// reviewed, because a decision has its own lifecycle and its own audit: who
/// decided, when, and on what grounds. Collapsing it into Collection would put
/// a reviewer's identity inside a curator's aggregate and make "who rejected
/// edition 3" unanswerable after edition 4.
/// </summary>
public sealed class Review : AggregateRoot<Guid>
{
    private Review()
    {
    }

    private Review(ReviewSubject subject, Guid subjectId, DateTimeOffset openedAt)
    {
        Id = Guid.NewGuid();
        Subject = subject;
        SubjectId = subjectId;
        OpenedAt = openedAt;
        Outcome = ReviewOutcome.Pending;
    }

    public ReviewSubject Subject { get; private set; }

    public Guid SubjectId { get; private set; }

    public ReviewOutcome Outcome { get; private set; }

    public string? ReviewerId { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public static Review Open(ReviewSubject subject, Guid subjectId, DateTimeOffset openedAt) =>
        new(subject, subjectId, openedAt);

    public void Approve(string reviewerId, DateTimeOffset decidedAt)
    {
        RequirePending();

        ReviewerId = reviewerId;
        Outcome = ReviewOutcome.Approved;
        DecidedAt = decidedAt;
    }

    public void Reject(string reviewerId, string reason, DateTimeOffset decidedAt)
    {
        RequirePending();

        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("A rejection must carry a reason the curator can act on.");

        ReviewerId = reviewerId;
        Reason = reason;
        Outcome = ReviewOutcome.Rejected;
        DecidedAt = decidedAt;
    }

    /// <summary>
    /// A decided review is final. Re-deciding would make the audit trail a
    /// lie, and it is also what makes an idempotent consumer safe: a
    /// redelivered decision hits this and is refused rather than silently
    /// overwriting who decided what.
    /// </summary>
    private void RequirePending()
    {
        if (Outcome != ReviewOutcome.Pending)
            throw new DomainException($"This review was already {Outcome}.");
    }
}
