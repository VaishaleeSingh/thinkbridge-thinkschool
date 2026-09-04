using System.ComponentModel.DataAnnotations;

namespace QuotesApi.Resilience;

/// <summary>
/// Every parameter of the outbound-HTTP resilience pipeline, bound from the
/// "Resilience" section and validated at startup like CacheOptions and
/// OutboxOptions.
///
/// WHY THIS CLASS EXISTS AT ALL, given that Day 5 shipped a working pipeline
/// with the numbers written inline:
///
/// Day 5's own test file explains it, unintentionally. It says the circuit
/// breaker is not tested because opening it "needs at least MinimumThroughput
/// (10) failing calls inside a 30 second window and would trade seconds of
/// test runtime for a property that is a configured constant rather than
/// logic." That was true, and it is exactly the shape of an argument that
/// leaves the most important strategy in the pipeline unproven: the breaker
/// was untestable BECAUSE its parameters were constants, and "it is only a
/// constant" was then used as the reason not to test it.
///
/// Binding the parameters is therefore not housekeeping. It is the change that
/// makes "prove the circuit opens under sustained failure and recovers" a
/// sub-second deterministic test instead of a ten-second sleep, and it is the
/// first step of Day 22 for that reason.
///
/// The DEFAULTS below reproduce Day 5's inline constants exactly. That is a
/// requirement, not a coincidence: the Day 5 tests must pass unmodified after
/// this refactor, and if they do not, the refactor changed behaviour it was not
/// supposed to change.
/// </summary>
public sealed class ResilienceOptions : IValidatableObject
{
    public const string SectionName = "Resilience";

    /// <summary>
    /// The promise made to the caller: the outermost cap, covering every
    /// attempt and every backoff delay between them.
    ///
    /// An inbound request waiting on token validation is not held longer than
    /// this, whatever happens inside the pipeline.
    /// </summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The innermost cap, applied to each individual attempt.
    ///
    /// Must be meaningfully smaller than TotalTimeout. Equal, and the first
    /// attempt can consume the whole budget, leaving no room for the retries
    /// above it to run at all -- at which point the retry configuration is
    /// decorative. Validate() enforces that rather than trusting a comment,
    /// which is the difference between this and Day 5.
    /// </summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(3);

    public RetryOptions Retry { get; set; } = new();

    public CircuitBreakerOptions CircuitBreaker { get; set; } = new();

    public BulkheadOptions Bulkhead { get; set; } = new();

    public sealed class RetryOptions
    {
        /// <summary>Retries AFTER the first attempt. 3 means up to 4 calls.</summary>
        [Range(0, 10)]
        public int MaxAttempts { get; set; } = 3;

        /// <summary>Exponential base delay, jittered.</summary>
        public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// THE GATE. When true -- the default, and the only defensible default
        /// -- only requests whose method is idempotent, or which carry an
        /// Idempotency-Key header, are retried. See IdempotencyPredicate.
        ///
        /// Setting this false is a deliberate choice to re-send writes after a
        /// 5xx, and a 5xx does not tell you whether the far end processed the
        /// request before it fell over. It is logged at startup when false, so
        /// the choice is visible in the logs of whoever has to explain the
        /// duplicate.
        /// </summary>
        public bool IdempotentOnly { get; set; } = true;
    }

    public sealed class CircuitBreakerOptions
    {
        /// <summary>
        /// Fraction of calls in the sampling window that must fail before the
        /// circuit opens.
        /// </summary>
        [Range(0.01, 1.0)]
        public double FailureRatio { get; set; } = 0.5;

        /// <summary>
        /// The minimum number of calls in the window before the ratio is acted
        /// on. This is the guard that makes a ratio mean anything: without it,
        /// one failure out of one call is a 100% failure rate.
        ///
        /// Range starts at 2 deliberately. A MinimumThroughput of 1 is worse
        /// than having no breaker at all, because it converts a single blip
        /// into BreakDuration of guaranteed, self-inflicted failure.
        /// </summary>
        [Range(2, 10_000)]
        public int MinimumThroughput { get; set; } = 10;

        /// <summary>The rolling window the ratio is computed over.</summary>
        public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How long the circuit stays open before admitting one trial request.
        /// </summary>
        public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(15);
    }

    public sealed class BulkheadOptions
    {
        /// <summary>
        /// Concurrent in-flight operations allowed against this dependency.
        ///
        /// Sized from what the dependency actually receives rather than from a
        /// round number: the entra-id client fetches OIDC metadata and a key
        /// set that ConfigurationManager caches for 24 hours, so steady-state
        /// traffic is a handful of requests per instance per day plus a small
        /// burst when the cache is cold or a signing key rolls. Four permits is
        /// generous for that and tight enough to shed under the case worth
        /// defending against -- metadata refresh hanging while inbound token
        /// validations queue up behind it.
        ///
        /// It lives in configuration because it is a judgement about traffic,
        /// not a fact about the code.
        /// </summary>
        [Range(1, 1_000)]
        public int PermitLimit { get; set; } = 4;

        /// <summary>
        /// Waiters admitted before the limiter starts shedding. Zero means fail
        /// immediately when all permits are taken.
        /// </summary>
        [Range(0, 10_000)]
        public int QueueLimit { get; set; } = 8;
    }

    /// <summary>
    /// The cross-field rules. These are the ones that cannot be expressed as
    /// per-property attributes, because each is only wrong in relation to
    /// another value -- which is precisely the class of misconfiguration that
    /// survives review and then behaves strangely in production.
    ///
    /// Validator.TryValidateObject does NOT recurse into nested objects, so the
    /// children's attributes are checked here explicitly rather than assumed.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (TotalTimeout <= TimeSpan.Zero)
            yield return new ValidationResult(
                "Resilience:TotalTimeout must be greater than zero.",
                new[] { nameof(TotalTimeout) });

        if (AttemptTimeout <= TimeSpan.Zero)
            yield return new ValidationResult(
                "Resilience:AttemptTimeout must be greater than zero.",
                new[] { nameof(AttemptTimeout) });

        // The rule Day 5 could only write as a comment.
        if (AttemptTimeout >= TotalTimeout)
            yield return new ValidationResult(
                $"Resilience:AttemptTimeout ({AttemptTimeout}) must be strictly less than " +
                $"Resilience:TotalTimeout ({TotalTimeout}). Equal or greater means the first " +
                "attempt can consume the entire budget, so no retry can ever run and the " +
                "retry configuration is decorative.",
                new[] { nameof(AttemptTimeout), nameof(TotalTimeout) });

        if (Retry.MaxAttempts is < 0 or > 10)
            yield return new ValidationResult(
                "Resilience:Retry:MaxAttempts must be between 0 and 10.",
                new[] { nameof(Retry) });

        if (Retry.BaseDelay < TimeSpan.Zero)
            yield return new ValidationResult(
                "Resilience:Retry:BaseDelay cannot be negative.",
                new[] { nameof(Retry) });

        if (CircuitBreaker.FailureRatio is <= 0 or > 1)
            yield return new ValidationResult(
                "Resilience:CircuitBreaker:FailureRatio must be greater than 0 and at most 1.",
                new[] { nameof(CircuitBreaker) });

        if (CircuitBreaker.MinimumThroughput < 2)
            yield return new ValidationResult(
                "Resilience:CircuitBreaker:MinimumThroughput must be at least 2. A value of 1 " +
                "opens the circuit on a single failure, which converts one blip into a full " +
                "BreakDuration of self-inflicted outage.",
                new[] { nameof(CircuitBreaker) });

        if (CircuitBreaker.SamplingDuration < TimeSpan.FromMilliseconds(500))
            yield return new ValidationResult(
                "Resilience:CircuitBreaker:SamplingDuration must be at least 500ms (Polly's own " +
                "lower bound).",
                new[] { nameof(CircuitBreaker) });

        if (CircuitBreaker.BreakDuration < TimeSpan.FromMilliseconds(500))
            yield return new ValidationResult(
                "Resilience:CircuitBreaker:BreakDuration must be at least 500ms (Polly's own " +
                "lower bound).",
                new[] { nameof(CircuitBreaker) });

        if (Bulkhead.PermitLimit < 1)
            yield return new ValidationResult(
                "Resilience:Bulkhead:PermitLimit must be at least 1. Zero permits is not a " +
                "bulkhead, it is an outage.",
                new[] { nameof(Bulkhead) });

        if (Bulkhead.QueueLimit < 0)
            yield return new ValidationResult(
                "Resilience:Bulkhead:QueueLimit cannot be negative.",
                new[] { nameof(Bulkhead) });
    }

    /// <summary>
    /// True when the retry budget cannot actually be spent inside the total
    /// timeout. This is LEGAL and usually intended -- the total timeout is
    /// meant to be the binding constraint -- but it is worth one startup log
    /// line, because someone reading MaxAttempts=3 will otherwise believe
    /// three retries are promised when the wall clock allows one.
    /// </summary>
    public bool RetryBudgetExceedsTotalTimeout()
    {
        var worstCaseAttempts = AttemptTimeout * (Retry.MaxAttempts + 1);
        var worstCaseBackoff = Retry.BaseDelay * Retry.MaxAttempts;

        return worstCaseAttempts + worstCaseBackoff > TotalTimeout;
    }
}
