using Microsoft.Extensions.Http.Resilience;
using Polly;

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
/// fails to validate. Nothing else in this project calls another service:
/// the database is EF Core over SQLite (in-process, not HTTP), and the
/// only other network dependency, Application Insights, is owned by the
/// Azure Monitor exporter, which has its own transmission and retry
/// pipeline and is not routed through IHttpClientFactory.
///
/// WHY A NAMED CLIENT AND NOT options.BackchannelHttpHandler. The
/// JwtBearer handler will build its own HttpClient if none is supplied,
/// and that client has no retry, no circuit breaker, and a single 60
/// second timeout. Registering a named client here and assigning it to
/// JwtBearerOptions.Backchannel (see InfrastructureExtensions) puts the
/// metadata fetch behind the same pipeline any other dependency would
/// get, and makes the policy testable without standing up the
/// authentication stack.
/// </summary>
public static class ResilienceExtensions
{
    /// <summary>
    /// The name of the HttpClient used for Entra ID metadata and key
    /// fetches. Referenced by InfrastructureExtensions when it assigns
    /// JwtBearerOptions.Backchannel, and by the unit tests.
    /// </summary>
    public const string EntraIdClientName = "entra-id";

    public static IServiceCollection AddResilientHttpClients(this IServiceCollection services)
    {
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
                // 10 second total timeout below is the real ceiling.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .AddResilienceHandler("default", (builder, context) =>
            {
                var logger = context.ServiceProvider
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("QuotesApi.Resilience.EntraId");

                // ORDER IS THE POLICY. Strategies added first sit OUTSIDE
                // the ones added after them, so this reads outermost-first:
                //
                //   total timeout  ->  retry  ->  circuit breaker  ->  attempt timeout  ->  the request
                //
                // Getting this backwards is the classic mistake. If the
                // timeout were added last (innermost), it would cap each
                // attempt but never the operation, and three retries with
                // backoff could run far past ten seconds while the caller
                // waits. If the circuit breaker sat outside the retry, a
                // single burst of retries against one dead host would
                // count as one failure instead of several, and the breaker
                // would take much longer to notice.

                builder
                    // 1. TOTAL TIMEOUT -- the promise made to the caller.
                    // Ten seconds covers everything below it: every
                    // attempt, every backoff delay between them. Whatever
                    // happens inside, an inbound request waiting on token
                    // validation is not held longer than this.
                    .AddTimeout(new HttpTimeoutStrategyOptions
                    {
                        Name = "total-timeout",
                        Timeout = TimeSpan.FromSeconds(10)
                    })

                    // 2. RETRY -- 3 attempts after the first, exponential
                    // backoff, jittered.
                    //
                    // UseJitter matters more than it looks: without it,
                    // every instance that saw the same outage retries at
                    // exactly the same moments, and the recovering service
                    // is hit by a synchronized wave rather than a spread
                    // of traffic. With MaxReplicas 5 on this container app
                    // (infra/resources.bicep) that is five clients, but
                    // the same reasoning is why the option exists at all.
                    //
                    // ShouldHandle is deliberately left at its default,
                    // which handles 5xx, 408, HttpRequestException and
                    // timeouts raised by the inner timeout strategy -- and
                    // notably does NOT retry 4xx. Retrying a 401 or a 404
                    // is just three more ways to get the same answer.
                    .AddRetry(new HttpRetryStrategyOptions
                    {
                        Name = "retry",
                        MaxRetryAttempts = 3,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        Delay = TimeSpan.FromSeconds(1),

                        // "Log every retry; never silently swallow
                        // failures." A retry that nobody records is an
                        // outage that shows up only as latency: the call
                        // eventually succeeds, the dashboard stays green,
                        // and the fact that the dependency needed three
                        // attempts is lost. Warning, not Information --
                        // this is a dependency misbehaving, not routine.
                        OnRetry = args =>
                        {
                            logger.LogWarning(
                                "Entra ID metadata request failed, retrying. Attempt {AttemptNumber} of {MaxAttempts}, waiting {Delay}. Outcome: {Outcome}",
                                args.AttemptNumber + 1,
                                3,
                                args.RetryDelay,
                                args.Outcome.Exception?.Message
                                    ?? args.Outcome.Result?.StatusCode.ToString()
                                    ?? "unknown");

                            return default;
                        }
                    })

                    // 3. CIRCUIT BREAKER -- stop calling a service that is
                    // clearly down.
                    //
                    // Opens when at least half the calls in a 30 second
                    // window fail. MinimumThroughput is the guard that
                    // makes a ratio meaningful: without it, one failure
                    // out of one call is a 100% failure rate and the
                    // breaker opens on a single blip. Ten calls in the
                    // window is the smallest sample worth acting on.
                    //
                    // While open, calls fail immediately with
                    // BrokenCircuitException instead of waiting for a
                    // timeout -- which is the entire point. A dead
                    // dependency should cost microseconds, not ten
                    // seconds of held threads per request.
                    .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                    {
                        Name = "circuit-breaker",
                        FailureRatio = 0.5,
                        SamplingDuration = TimeSpan.FromSeconds(30),
                        MinimumThroughput = 10,
                        BreakDuration = TimeSpan.FromSeconds(15),

                        OnOpened = args =>
                        {
                            logger.LogError(
                                "Entra ID circuit breaker OPENED for {BreakDuration}. Entra-issued tokens cannot be validated until it closes.",
                                args.BreakDuration);
                            return default;
                        },
                        OnClosed = args =>
                        {
                            logger.LogInformation(
                                "Entra ID circuit breaker closed. Metadata requests are flowing again.");
                            return default;
                        },
                        OnHalfOpened = args =>
                        {
                            logger.LogInformation(
                                "Entra ID circuit breaker half-open -- letting one trial request through.");
                            return default;
                        }
                    })

                    // 4. ATTEMPT TIMEOUT -- innermost, so it applies to
                    // each individual try.
                    //
                    // Three seconds, not ten: a per-attempt timeout equal
                    // to the total timeout would let the first attempt
                    // consume the entire budget and leave no room for the
                    // retries above to run at all. A hung connection is
                    // abandoned quickly and retried, which is exactly the
                    // failure mode retries exist for.
                    .AddTimeout(new HttpTimeoutStrategyOptions
                    {
                        Name = "attempt-timeout",
                        Timeout = TimeSpan.FromSeconds(3)
                    });
            });

        return services;
    }
}
