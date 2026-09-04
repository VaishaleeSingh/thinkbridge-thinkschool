using QuotesPlatform.SharedKernel;

namespace QuotesPlatform.Modules.Curation.Domain;

public sealed class CollectionMember : Entity<Guid>
{
    private CollectionMember()
    {
        UserId = null!;
    }

    internal CollectionMember(string userId, CollectionRole role)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new DomainException("A member must have a user id.");

        Id = Guid.NewGuid();
        UserId = userId;
        Role = role;
    }

    public string UserId { get; private set; }

    public CollectionRole Role { get; private set; }
}
