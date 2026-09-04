using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.RateLimiting;
using QuotesApi.Resilience;

namespace QuotesApi.Extensions;

/// <summary>
/// Resilience for this API's outbound HTTP calls.
///
/// WHICH CALLS. This API makes exactly one category of outbound HTTP
/// request, and it is easy to miss because no line of application code
/// issues it: the "EntraId" JwtBearer handler fetches Entra ID's OpenID
/// Connect metadata document and its JSON Web Key Set from
/// AzureAd:Authority, and refreshes them periodically. That fetch is what
/// makes signature validation possible, so when login.microsoftonline.com
/// is slow or briefly unavailable, every Entra-issued token in flight
/// fails to validate.
///
/// WHAT IS DELIBERATELY NOT WRAPPED, because "wrap an outbound dependency
/// with Polly" is easy to over-apply and the most common production mistake
/// with Polly is not a missing policy but two retry policies stacked on the
/// same call, each unaware of the other:
///
///   - SQLite / SQL Server: EF Core's own EnableRetryOnFailure execution
///     strategy already owns this, and a Polly retry above it would multiply
///     attempts. A retry wrapped around an open transaction is a correctness
///     bug, not a resilience feature -- see QuoteWriteService.
///   - Azure Service Bus: ServiceBusClientOptions.RetryOptions retries with
///     backoff inside the SDK. Polly on top gives 3 x 3 attempts and a
///     breaker that cannot see the SDK's internal ones.
///   - Redis L2: Day 21 already chose the right behaviour --
///     AbortOnConnectFail=false and degrade to L1 -- and HybridCache
///     swallows an L2 failure by design. A breaker here would be a second,
///     redundant open/closed state.
///   - Application Insights: not routed through IHttpClientFactory; the
///     Azure Monitor exporter owns its own transmission and retry.
///
/// WHY A NAMED CLIENT AND NOT options.BackchannelHttpHandler. The
/// JwtBearer handler will build its own HttpClient if none is supplied,
/// and that client has no retry, no circuit breaker, and a single 60
/// second timeout. Registering a named client here and assigning it to
/// JwtBearerOptions.Backchannel (see InfrastructureExtensions) puts the
/// metadata fetch behind the same pipeline any other dependency would
/// get, and makes the policy testable without standing up the
/// authentication stack.
///
/// DAY 22 CHANGED THREE THINGS about the Day 5 version of this file:
///   1. Every number now comes from ResilienceOptions instead of being
///      inline. That is what makes the circuit breaker testable in under a
///      second, which is what makes it provable at all.
///   2. A bulkhead was added (the rate-limiter strategy with a concurrency
///      limiter -- Polly v8 has no strategy called "bulkhead").
///   3. The retry is now gated on idempotency. It was not, and that was a
///      latent defect rather than a missing feature: see IdempotencyPredicate.
/// </summary>
public static class ResilienceExtensions
{
    /// <summary>
    /// The name of the HttpClient used for Entra ID metadata and key
    /// fetches. Referenced by InfrastructureExtensions when it assigns
    /// JwtBearerOptions.Backchannel, and by the unit tests.
    /// </summary>
    public const string EntraIdClientName = ResiliencePipelineNames.EntraIdPipeline;

    /// <summary>
    /// Registers the resilient clients with DEFAULT policy values.
    ///
    /// This overload exists so the Day 5 tests -- which build a bare
    /// ServiceCollection with no IConfiguration at all -- keep compiling and
    /// passing unmodified through the options refactor. That is the check on
    /// the refactor: if those tests had to be edited, the defaults in
    /// ResilienceOptions no longer reproduce the Day 5 constants and the
    /// refactor changed behaviour it was not supposed to change.
    /// </summary>
    public static IServiceCollection AddResilientHttpClients(this IServiceCollection services) =>
        services.AddResilientHttpClients(configuration: null);

    public static IServiceCollection AddResilientHttpClients(
        this IServiceCollection services,
        IConfiguration? configuration)
    {
        var optionsBuilder = services.AddOptions<ResilienceOptions>();

        if (configuration is not null)
            optionsBuilder.Bind(configuration.GetSection(ResilienceOptions.SectionName));

        // ValidateDataAnnotations picks up the [Range] attributes AND
        // IValidatableObject.Validate, which is where the cross-field rules
        // live -- the two timeouts are each individually plausible and only
        // wrong in relation to each other.
        optionsBuilder
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // TryAdd, and singletons: the state provider is only useful to a
        // reader that holds the same instance the pipeline was built with,
        // and both overloads of this method may be reached.
        services.TryAddSingleton<CircuitBreakerRegistry>();
        services.TryAddSingleton<ResilienceMetrics>();

        services
            .AddHttpClient(EntraIdClientName, client =>
            {
                // HttpClient.Timeout is a single, non-negotiable cap that
                // throws TaskCanceledException with no distinction between
                // "the attempt timed out" and "the caller cancelled". The
                // resilience pipeline below owns timeouts instead -- one
                // per attempt and one for the whole operation -- and it
                // cannot express the per-attempt half if HttpClient's own
                // timeout fires first. Disabling it here is what hands
                // timeout control to Polly rather than removing it: the
                // total timeout below is the real ceiling.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .AddResilienceHandler(ResiliencePipelineNames.EntraIdPipeline, (builder, context) =>
            {
                // Not named "services": a local cannot shadow the enclosing
                // method's parameter of that name (CS0136).
                var sp = context.ServiceProvider;

                var options = sp
                    .GetRequiredService<IOptions<ResilienceOptions>>().Value;

                var metrics = sp.GetRequiredService<ResilienceMetrics>();
                var circuitBreaker = sp.GetRequiredService<CircuitBreakerRegistry>();

                var logger = sp
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("QuotesApi.Resilience.EntraId");

                const string pipeline = ResiliencePipelineNames.EntraIdPipeline;

                if (!options.Retry.IdempotentOnly)
                {
                    // Logged, not silent. Someone has switched off the gate,
                    // which means writes will be re-sent after a 5xx, and the
                    // person explaining the duplicate later should be able to
                    // find that decision in the logs of the process that made
                    // it.
                    logger.LogWarning(
                        "Resilience:Retry:IdempotentOnly is false. Non-idempotent requests " +
                        "(POST, PATCH without an Idempotency-Key) WILL be retried after a " +
                        "transient failure, which can duplicate a write the far end already " +
                        "processed.");
                }

                if (options.RetryBudgetExceedsTotalTimeout())
                {
                    // Legal and usually intended -- the total timeout is meant
                    // to be the binding constraint -- but worth one line, so
                    // nobody reads MaxAttempts as a promise the wall clock
                    // cannot keep.
                    logger.LogInformation(
                        "Resilience: the retry budget ({Attempts} attempts of {AttemptTimeout} " +
                        "plus backoff) exceeds Resilience:TotalTimeout ({TotalTimeout}), so the " +
                        "total timeout is the binding constraint and fewer retries than " +
                        "configured may actually run.",
                        options.Retry.MaxAttempts + 1,
                        options.AttemptTimeout,
                        options.TotalTimeout);
                }

                // ORDER IS THE POLICY. Strategies added first sit OUTSIDE
                // the ones added after them, so this reads outermost-first:
                //
                //   total timeout -> bulkhead -> retry -> circuit breaker
                //       -> attempt timeout -> the request
                //
                // Getting this backwards is the classic mistake. If the
                // attempt timeout were added first (outermost), it would cap
                // the operation but never an individual try, and three
                // retries with backoff could run far past the total budget
                // while the caller waits. If the circuit breaker sat outside
                // the retry, a single burst of retries against one dead host
                // would count as ONE failure instead of several, and the
                // breaker would take far longer to notice.
                //
                // The bulkhead's position is the only genuinely arguable one,
                // and both halves of the argument matter:
                //
                //   OUTSIDE THE RETRY. A bulkhead bounds how much of this
                //   process is tied up waiting on one dependency, and one
                //   logical operation should hold one permit for its whole
                //   life -- retries and backoff delays included -- because
                //   that is the resource actually being consumed. Placed
                //   inside the retry, each attempt would acquire and release
                //   separately, and worse: a RateLimiterRejectedException
                //   from a full bulkhead would land in the retry's
                //   ShouldHandle and be treated as a transient failure.
                //   Retrying a load-shed rejection is the definition of
                //   making an overload worse.
                //
                //   Being outside the breaker matters for the same reason in
                //   the other direction: the breaker cannot see a rejection
                //   the limiter raised above it, so our own back-pressure can
                //   never open the circuit. That property is structural here,
                //   not a predicate someone has to remember to write.
                //
                //   INSIDE THE TOTAL TIMEOUT. Waiting for a permit is
                //   waiting. With the limiter outermost, a caller could queue
                //   with nothing bounding that wait, and the promise made to
                //   an inbound request would only hold for requests that got
                //   lucky. QueueLimit bounds how MANY wait; the total timeout
                //   bounds how LONG any one of them waits.

                builder
                    // 1. TOTAL TIMEOUT -- the promise made to the caller.
                    // Covers everything below it: the permit wait, every
                    // attempt, every backoff delay between them.
                    .AddTimeout(new HttpTimeoutStrategyOptions
                    {
                        Name = ResiliencePipelineNames.TotalTimeout,
                        Timeout = options.TotalTimeout
                    })

                    // 2. BULKHEAD -- a cap on how much of this process can be
                    // tied up in this one dependency at once.
                    //
                    // Polly v8 has no BulkheadPolicy; v7's was replaced by the
                    // rate-limiter strategy over System.Threading.RateLimiting.
                    // A bulkhead is that strategy with a ConcurrencyLimiter:
                    // PermitLimit concurrent executions, QueueLimit waiters,
                    // everything beyond that shed immediately.
                    //
                    // Shedding is the system working. It is not a dependency
                    // failure, and it must not be counted as one -- see the
                    // ordering note above for why that is structural.
                    .AddRateLimiter(new HttpRateLimiterStrategyOptions
                    {
                        Name = ResiliencePipelineNames.Bulkhead,
                        DefaultRateLimiterOptions = new ConcurrencyLimiterOptions
                        {
                            PermitLimit = options.Bulkhead.PermitLimit,
                            QueueLimit = options.Bulkhead.QueueLimit,

                            // Oldest-first: a waiter that has already paid the
                            // most latency should not be starved by arrivals
                            // behind it.
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                        },

                        OnRejected = args =>
                        {
                            metrics.RecordBulkheadRejection(pipeline);

                            logger.LogWarning(
                                "Entra ID request shed by the bulkhead: {PermitLimit} permits and " +
                                "{QueueLimit} queue slots are all in use. This is back-pressure, " +
                                "not a dependency failure, and it does not count towards the " +
                                "circuit breaker.",
                                options.Bulkhead.PermitLimit,
                                options.Bulkhead.QueueLimit);

                            return default;
                        }
                    })

                    // 3. RETRY -- exponential backoff, jittered, and gated on
                    // idempotency.
                    //
                    // UseJitter matters more than it looks: without it, every
                    // instance that saw the same outage retries at exactly the
                    // same moments, and the recovering service is hit by a
                    // synchronised wave rather than a spread of traffic. With
                    // MaxReplicas 5 on this container app
                    // (infra/resources.bicep) that is five clients, but the
                    // same reasoning is why the option exists at all.
                    .AddRetry(new HttpRetryStrategyOptions
                    {
                        Name = ResiliencePipelineNames.Retry,
                        MaxRetryAttempts = options.Retry.MaxAttempts,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        Delay = options.Retry.BaseDelay,

                        // THE GATE. Day 5 left this at its default, which
                        // handles 5xx, 408, HttpRequestException and inner
                        // timeouts -- regardless of HTTP method. That was
                        // harmless only because the sole caller issues GETs,
                        // which is a property of the caller and not of the
                        // pipeline. See IdempotencyPredicate for what
                        // "idempotent" is taken to mean and which assumption
                        // that inherits.
                        //
                        // Composed WITH the transient predicate rather than
                        // replacing it: a hand-rolled check would lose Polly's
                        // distinction between a timeout cancellation (which
                        // should be retried) and a caller's cancellation
                        // (which must fall straight through).
                        ShouldHandle = args =>
                        {
                            if (!HttpClientResiliencePredicates.IsTransient(args.Outcome))
                                return PredicateResult.False();

                            if (!options.Retry.IdempotentOnly)
                                return PredicateResult.True();

                            // An exception outcome carries no
                            // HttpResponseMessage, so the request has to come
                            // from the ResilienceContext in that case.
                            var request = args.Outcome.Result?.RequestMessage
                                          ?? args.Context.GetRequestMessage();

                            if (IdempotencyPredicate.IsRetryable(request))
                                return PredicateResult.True();

                            // The only evidence the gate is doing anything.
                            // A declined retry is a non-event to Polly -- no
                            // retry happened, so its own telemetry emits
                            // nothing -- which would leave a gate that is
                            // broken indistinguishable from a gate that is
                            // never triggered.
                            metrics.RecordSuppressedRetry(
                                pipeline,
                                request?.Method.Method ?? "unknown");

                            logger.LogWarning(
                                "Entra ID request failed transiently but was NOT retried: " +
                                "{Method} is not idempotent and carried no {Header}. Retrying it " +
                                "could duplicate an operation the far end already performed.",
                                request?.Method.Method ?? "unknown",
                                IdempotencyPredicate.IdempotencyKeyHeader);

                            return PredicateResult.False();
                        },

                        // "Log every retry; never silently swallow
                        // failures." A retry that nobody records is an
                        // outage that shows up only as latency: the call
                        // eventually succeeds, the dashboard stays green,
                        // and the fact that the dependency needed three
                        // attempts is lost. Warning, not Information --
                        // this is a dependency misbehaving, not routine.
                        OnRetry = args =>
                        {
                            var outcome = args.Outcome.Exception?.Message
                                ?? args.Outcome.Result?.StatusCode.ToString()
                                ?? "unknown";

                            metrics.RecordRetry(pipeline, outcome);

                            logger.LogWarning(
                                "Entra ID metadata request failed, retrying. Attempt {AttemptNumber} of {MaxAttempts}, waiting {Delay}. Outcome: {Outcome}",
                                args.AttemptNumber + 1,
                                options.Retry.MaxAttempts,
                                args.RetryDelay,
                                outcome);

                            return default;
                        }
                    })

                    // 4. CIRCUIT BREAKER -- stop calling a service that is
                    // clearly down.
                    //
                    // Opens when at least FailureRatio of the calls in the
                    // sampling window fail. MinimumThroughput is the guard
                    // that makes a ratio meaningful: without it, one failure
                    // out of one call is a 100% failure rate and the breaker
                    // opens on a single blip. ResilienceOptions refuses a
                    // value below 2 for that reason.
                    //
                    // While open, calls fail immediately with
                    // BrokenCircuitException instead of waiting for a
                    // timeout -- which is the entire point. A dead dependency
                    // should cost microseconds, not a full attempt timeout of
                    // held threads per request.
                    //
                    // StateProvider and ManualControl are Day 22's addition
                    // and the reason this strategy is finally provable: the
                    // state provider lets a test and a diagnostics endpoint
                    // ASK the breaker what state it is in, rather than
                    // inferring it from how often a stub was called -- an
                    // inference an open breaker, a full bulkhead and a
                    // declined retry predicate all satisfy identically.
                    .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                    {
                        Name = ResiliencePipelineNames.CircuitBreaker,
                        FailureRatio = options.CircuitBreaker.FailureRatio,
                        SamplingDuration = options.CircuitBreaker.SamplingDuration,
                        MinimumThroughput = options.CircuitBreaker.MinimumThroughput,
                        BreakDuration = options.CircuitBreaker.BreakDuration,

                        StateProvider = circuitBreaker.State,
                        ManualControl = circuitBreaker.ManualControl,

                        OnOpened = args =>
                        {
                            metrics.RecordCircuitOpened(pipeline);

                            logger.LogError(
                                "Entra ID circuit breaker OPENED for {BreakDuration}. Entra-issued tokens cannot be validated until it closes.",
                                args.BreakDuration);
                            return default;
                        },
                        OnClosed = args =>
                        {
                            metrics.RecordCircuitClosed(pipeline);

                            logger.LogInformation(
                                "Entra ID circuit breaker closed. Metadata requests are flowing again.");
                            return default;
                        },
                        OnHalfOpened = args =>
                        {
                            metrics.RecordCircuitHalfOpened(pipeline);

                            logger.LogInformation(
                                "Entra ID circuit breaker half-open -- letting one trial request through.");
                            return default;
                        }
                    })

                    // 5. ATTEMPT TIMEOUT -- innermost, so it applies to
                    // each individual try.
                    //
                    // Smaller than the total on purpose: a per-attempt timeout
                    // equal to the total would let the first attempt consume
                    // the entire budget and leave no room for the retries
                    // above it to run at all. ResilienceOptions.Validate
                    // enforces the inequality rather than trusting this
                    // comment. A hung connection is abandoned quickly and
                    // retried, which is exactly the failure mode retries
                    // exist for.
                    .AddTimeout(new HttpTimeoutStrategyOptions
                    {
                        Name = ResiliencePipelineNames.AttemptTimeout,
                        Timeout = options.AttemptTimeout
                    });
            });

        return services;
    }
}
