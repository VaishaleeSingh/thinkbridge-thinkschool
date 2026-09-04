using Polly.CircuitBreaker;

namespace QuotesApi.Resilience;

/// <summary>
/// Holds the circuit breaker's state provider and manual control, so that
/// something outside the pipeline can ask the breaker what state it is in.
///
/// THIS IS THE CHANGE THAT MAKES THE BREAKER PROVABLE. Day 5's breaker was
/// configured, logged three messages, and was otherwise invisible: nothing
/// could ask it "are you open right now?". A test could only infer its state
/// from how many times a stub handler was called, and an inference about a
/// breaker is not a statement about it -- the same call count is produced by
/// an open breaker, a full bulkhead, and a retry predicate that declined.
///
/// CircuitBreakerStateProvider closes that gap: it reports CircuitState
/// directly (Closed / Open / HalfOpen / Isolated), which is what the Day 22
/// lifecycle test asserts on and what the diagnostics endpoint reports.
///
/// CircuitBreakerManualControl can isolate the circuit on demand. It exists
/// for the Development-only demonstration route, so a live walkthrough does
/// not require making login.microsoftonline.com fail. It is deliberately NOT
/// used by the automated proof: isolating a breaker by hand demonstrates that
/// the manual control works, not that sustained failure opens the circuit.
/// Those are different claims and only the second one is the task.
///
/// Registered as a singleton because a state provider bound to a strategy
/// instance is only useful if the reader holds the same instance the pipeline
/// was built with.
/// </summary>
public sealed class CircuitBreakerRegistry
{
    public CircuitBreakerStateProvider State { get; } = new();

    public CircuitBreakerManualControl ManualControl { get; } = new();

    /// <summary>
    /// The circuit's state as a number, for the observable gauge: 0 closed,
    /// 1 half-open, 2 open, 3 isolated.
    ///
    /// Ordered by severity rather than by the enum's declaration order, so a
    /// dashboard can alert on "greater than zero" and a graph of it reads the
    /// way an operator expects: higher is worse.
    /// </summary>
    public int StateAsGaugeValue => State.CircuitState switch
    {
        CircuitState.Closed => 0,
        CircuitState.HalfOpen => 1,
        CircuitState.Open => 2,
        CircuitState.Isolated => 3,
        _ => -1
    };

    public string StateName => State.CircuitState.ToString();
}
