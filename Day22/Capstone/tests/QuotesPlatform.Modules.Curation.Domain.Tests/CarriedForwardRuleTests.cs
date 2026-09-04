using FluentAssertions;
using QuotesPlatform.Modules.Curation.Domain;
using QuotesPlatform.SharedKernel;

namespace QuotesPlatform.Modules.Curation.Domain.Tests;

/// <summary>
/// The three rules carried unchanged from
/// Day7/piece2/QuotesApi/Models/Collection.cs.
///
/// They are tested here rather than assumed, because "carried forward" is a
/// claim: the capstone is a continuation, and a rewrite that quietly dropped a
/// rule the earlier days argued for would be a regression dressed as progress.
/// </summary>
public class CarriedForwardRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    private const string Owner = "user-owner";

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]
    public void A_name_shorter_than_three_characters_is_refused(string name)
    {
        var create = () => Collection.Create(name, Owner, Now);

        create.Should().Throw<DomainException>().WithMessage("*between 3 and 80*");
    }

    [Fact]
    public void A_name_longer_than_eighty_characters_is_refused()
    {
        var create = () => Collection.Create(new string('x', 81), Owner, Now);

        create.Should().Throw<DomainException>().WithMessage("*between 3 and 80*");
    }

    [Fact]
    public void The_same_quote_cannot_be_added_twice()
    {
        var collection = Collection.Create("No duplicates", Owner, Now);
        var quoteId = Guid.NewGuid();
        collection.AddItem(quoteId, "Seneca", "Every new beginning.", true, Owner, Now);

        var again = () => collection.AddItem(quoteId, "Seneca", "Every new beginning.", true, Owner, Now);

        again.Should().Throw<DomainException>().WithMessage("*already in the collection*");
    }

    [Fact]
    public void A_collection_cannot_hold_more_than_fifty_items()
    {
        var collection = Collection.Create("Fifty and one", Owner, Now);

        for (var i = 0; i < Collection.MaxItems; i++)
        {
            collection.AddItem(Guid.NewGuid(), "Author", $"Text {i}", true, Owner, Now);
        }

        var oneMore = () => collection.AddItem(Guid.NewGuid(), "Author", "One too many", true, Owner, Now);

        oneMore.Should().Throw<DomainException>().WithMessage("*more than 50 items*");
    }
}
