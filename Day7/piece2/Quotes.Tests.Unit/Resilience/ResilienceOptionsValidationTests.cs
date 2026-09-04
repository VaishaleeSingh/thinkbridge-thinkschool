using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using QuotesApi.Resilience;

namespace Quotes.Tests.Unit.Resilience;

/// <summary>
/// The cross-field rules, which are the only ones worth testing here: a
/// per-property [Range] attribute is the framework's job, but a value that is
/// only wrong in RELATION to another value is this class's job, and it is
/// exactly the kind of misconfiguration that survives review and then behaves
/// strangely in production.
/// </summary>
public class ResilienceOptionsValidationTests
{
    private static IReadOnlyList<ValidationResult> Validate(ResilienceOptions options)
    {
        var results = new List<ValidationResult>();

        // validateAllProperties: true is what makes Validator invoke
        // IValidatableObject as well as the attributes -- which is also how
        // ValidateDataAnnotations() reaches these rules at startup.
        Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        return results;
    }

    /// <summary>
    /// The defaults must reproduce Day 5's inline constants exactly. If this
    /// fails, the options refactor changed the policy rather than exposing it,
    /// and every Day 5 assertion is now describing something else.
    /// </summary>
    [Fact]
    public void Defaults_MatchTheDay5Policy_AndAreValid()
    {
        var options = new ResilienceOptions();

        options.TotalTimeout.Should().Be(TimeSpan.FromSeconds(10));
        options.AttemptTimeout.Should().Be(TimeSpan.FromSeconds(3));
        options.Retry.MaxAttempts.Should().Be(3);
        options.Retry.BaseDelay.Should().Be(TimeSpan.FromSeconds(1));
        options.CircuitBreaker.FailureRatio.Should().Be(0.5);
        options.CircuitBreaker.MinimumThroughput.Should().Be(10);
        options.CircuitBreaker.SamplingDuration.Should().Be(TimeSpan.FromSeconds(30));
        options.CircuitBreaker.BreakDuration.Should().Be(TimeSpan.FromSeconds(15));

        // Day 22's additions, which Day 5 had no equivalent for.
        options.Retry.IdempotentOnly.Should().BeTrue();
        options.Bulkhead.PermitLimit.Should().Be(4);
        options.Bulkhead.QueueLimit.Should().Be(8);

        Validate(options).Should().BeEmpty();
    }

    /// <summary>
    /// The rule Day 5 could only write as a comment. An attempt timeout equal
    /// to the total lets the first attempt consume the whole budget, so no
    /// retry can ever run and the retry configuration is decorative.
    /// </summary>
    [Theory]
    [InlineData("00:00:10", "00:00:10")]
    [InlineData("00:00:10", "00:00:30")]
    public void AttemptTimeout_NotLessThanTotalTimeout_IsRejected(string total, string attempt)
    {
        var options = new ResilienceOptions
        {
            TotalTimeout = TimeSpan.Parse(total),
            AttemptTimeout = TimeSpan.Parse(attempt)
        };

        Validate(options).Should().Contain(r =>
            r.ErrorMessage!.Contains("must be strictly less than"));
    }

    /// <summary>
    /// A MinimumThroughput of 1 is worse than having no breaker at all: it
    /// converts a single blip into a full BreakDuration of guaranteed,
    /// self-inflicted failure.
    /// </summary>
    [Fact]
    public void MinimumThroughput_BelowTwo_IsRejected()
    {
        var options = new ResilienceOptions();
        options.CircuitBreaker.MinimumThroughput = 1;

        Validate(options).Should().Contain(r =>
            r.ErrorMessage!.Contains("MinimumThroughput must be at least 2"));
    }

    [Fact]
    public void PermitLimit_OfZero_IsRejected()
    {
        var options = new ResilienceOptions();
        options.Bulkhead.PermitLimit = 0;

        Validate(options).Should().Contain(r =>
            r.ErrorMessage!.Contains("PermitLimit must be at least 1"));
    }

    /// <summary>
    /// Legal, and usually intended -- the total timeout is meant to be the
    /// binding constraint -- so it must NOT be a validation failure. It is
    /// reported instead, once, at startup, so nobody reads MaxAttempts as a
    /// promise the wall clock cannot keep.
    /// </summary>
    [Fact]
    public void ARetryBudgetLargerThanTheTotalTimeout_IsReported_ButNotInvalid()
    {
        var options = new ResilienceOptions
        {
            TotalTimeout = TimeSpan.FromSeconds(5),
            AttemptTimeout = TimeSpan.FromSeconds(3)
        };

        options.RetryBudgetExceedsTotalTimeout().Should().BeTrue();
        Validate(options).Should().BeEmpty();
    }

    [Fact]
    public void ATightBudget_IsNotReported()
    {
        var options = new ResilienceOptions
        {
            TotalTimeout = TimeSpan.FromSeconds(30),
            AttemptTimeout = TimeSpan.FromSeconds(2)
        };
        options.Retry.MaxAttempts = 2;
        options.Retry.BaseDelay = TimeSpan.FromSeconds(1);

        // 3 attempts x 2s + 2 backoffs x 1s = 8s, inside 30s.
        options.RetryBudgetExceedsTotalTimeout().Should().BeFalse();
    }
}
