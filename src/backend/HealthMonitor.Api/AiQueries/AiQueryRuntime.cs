using HealthMonitor.Application.AiQueries;
using HealthMonitor.Application.AiQueries.Ports;

namespace HealthMonitor.Api.AiQueries;

/// <summary>Owns shared AI admission resources until every admitted operation has actually terminated.</summary>
/// <remarks>This component is not registered or activated yet. Request contexts live only in returned request-scoped
/// generators and active calls, never as singleton identity. Shutdown rejects new work atomically but preserves
/// existing caller tokens. A bounded shutdown wait is not proof of cancellation, disposal, or stopped billing.
/// Deferred cleanup retains ownership until drain; process exit can still terminate unfinished work and release OS resources.</remarks>
internal sealed class AiQueryRuntime : IAsyncDisposable
{
    private static readonly TimeSpan MaximumShutdownWait = TimeSpan.FromSeconds(5);
    private readonly IStructuredQueryGenerator provider;
    private readonly IDisposable providerOwner;
    private readonly AiAdmissionPolicy policy;
    private readonly AiDailyBudgetStore budget;
    private readonly TimeProvider clock;
    private readonly ILogger<AiQueryRuntime> logger;
    private readonly TimeSpan shutdownWait;
    private readonly object sync = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource cleanupCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int active;
    private bool stopping;
    private Task? cleanupWork;
    private Task? shutdownWork;

    /// <summary>Validates all arguments before accepting exclusive ownership of provider, policy and counter.</summary>
    /// <param name="provider">Disposable provider adapter; no calls or I/O are performed by construction.</param>
    /// <param name="policy">Shared process admission policy transferred to this owner.</param>
    /// <param name="budget">Shared counter transferred to this owner, including its eventual lifetime lock.</param>
    /// <param name="clock">Borrowed UTC clock, the same instance used by the counter; never disposed here.</param>
    /// <param name="logger">Borrowed diagnostics sink; receives fixed text without exception objects or user data.</param>
    /// <param name="shutdownWait">Internal test seam permitting a shorter positive wait, never more than five seconds or an application setting.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The provider does not expose disposal ownership.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The wait is outside the supported bounded interval.</exception>
    internal AiQueryRuntime(IStructuredQueryGenerator provider, AiAdmissionPolicy policy, AiDailyBudgetStore budget,
        TimeProvider clock, ILogger<AiQueryRuntime> logger, TimeSpan? shutdownWait = null)
    {
        ArgumentNullException.ThrowIfNull(provider); ArgumentNullException.ThrowIfNull(policy); ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(logger);
        if (provider is not IDisposable owner) throw new ArgumentException("The provider must transfer disposal ownership.", nameof(provider));
        var wait = shutdownWait ?? MaximumShutdownWait;
        if (wait <= TimeSpan.Zero || wait > MaximumShutdownWait) throw new ArgumentOutOfRangeException(nameof(shutdownWait));
        this.provider = provider; providerOwner = owner; this.policy = policy; this.budget = budget;
        this.clock = clock; this.logger = logger; this.shutdownWait = wait;
    }

    /// <summary>Gets actual resource-cleanup completion, distinct from the bounded shutdown wait.</summary>
    /// <remarks>Pending means ownership is still retained. Faulted means at least one cleanup failed, with a fixed safe
    /// error; every resource cleanup was attempted. The owner observes this fault internally even if a host no longer awaits it.</remarks>
    internal Task CleanupCompletion => cleanupCompleted.Task;

    /// <summary>Creates a request-owned port wrapper without beginning work or retaining its context in singleton state.</summary>
    /// <param name="context">The actual current request containing the owned ingress identity feature.</param>
    /// <returns>A port to consume through the existing Application validator.</returns>
    /// <exception cref="ArgumentNullException">The context is null.</exception>
    internal IStructuredQueryGenerator CreateGenerator(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new RequestGenerator(this, context);
    }

    /// <summary>Counts the whole decorator operation before any admission, reservation or provider work.</summary>
    /// <param name="context">Request-owned context whose verified identity the decorator consumes.</param>
    /// <param name="prompt">Application-validated, trimmed text; this owner does not duplicate semantic validation.</param>
    /// <param name="vocabulary">The unchanged owned vocabulary passed to the provider port.</param>
    /// <param name="token">Original caller token, including precedence over a concurrent stop or provider fault.</param>
    /// <returns>The untrusted provider proposal, still requiring Application validation.</returns>
    private async Task<StructuredQueryProposal?> GenerateAsync(HttpContext context, string prompt, QueryVocabulary vocabulary, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            lock (sync)
            {
                token.ThrowIfCancellationRequested();
                if (stopping) throw new AiAdmissionRejectedException(AiAdmissionRejectedException.Rejection.AdmissionUnavailable);
                active++;
            }
            try
            {
                return await new AdmittedStructuredQueryGenerator(provider, policy, budget, context, clock)
                    .GenerateAsync(prompt, vocabulary, token);
            }
            finally
            {
                // The decorator has already released its lease, including after late cancellation or failure.
                lock (sync) { active--; if (stopping && active == 0) drained.TrySetResult(); }
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        { token.ThrowIfCancellationRequested(); throw; }
    }

    /// <summary>Stops new entry once and shares one bounded shutdown task between all callers.</summary>
    /// <returns>Completion after cleanup or the five-second maximum wait; consult CleanupCompletion for actual disposal.</returns>
    /// <exception cref="InvalidOperationException">Cleanup finished within the wait but at least one resource failed to dispose.</exception>
    /// <remarks>Timeout never releases resources beneath unfinished work. Existing calls are neither abandoned nor
    /// given replacement cancellation tokens. This cannot guarantee provider termination or billing cancellation.</remarks>
    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (shutdownWork is not null) return new(shutdownWork);
            stopping = true;
            if (active == 0) drained.TrySetResult();
            // Dispatch once, not per request: a synchronous provider Dispose must not block the host's bounded wait.
            cleanupWork = Task.Run(CleanupAfterDrainAsync);
            shutdownWork = AwaitShutdownAsync(cleanupWork);
            return new(shutdownWork);
        }
    }

    /// <summary>Bounds only the caller's wait while retaining the actual cleanup task and its resource ownership.</summary>
    private async Task AwaitShutdownAsync(Task cleanup)
    {
        using var timer = new CancellationTokenSource();
        // Use elapsed time rather than the UTC day clock so wall-clock adjustments cannot extend shutdown.
        if (await Task.WhenAny(cleanup, Task.Delay(shutdownWait, timer.Token)) == cleanup)
        {
            timer.Cancel(); await cleanup;
            await CleanupCompletion; // Propagate only the owned safe cleanup error, never a provider exception payload.
        }
        else SafeWarning("AI shutdown wait elapsed; resources remain owned until active work and cleanup finish.");
    }

    /// <summary>Waits for actual operations, then attempts every cleanup exactly once without leaking failure payloads.</summary>
    private async Task CleanupAfterDrainAsync()
    {
        await drained.Task;
        bool failed = false;
        try { providerOwner.Dispose(); } catch { failed = true; }
        try { policy.Dispose(); } catch { failed = true; }
        try { await budget.DisposeAsync(); } catch { failed = true; }
        if (failed)
        {
            SafeWarning("AI resource cleanup failed; every owned resource cleanup was attempted.");
            cleanupCompleted.TrySetException(new InvalidOperationException("AI resource cleanup failed."));
            _ = cleanupCompleted.Task.Exception; // A deferred fault remains observable without becoming unobserved after host shutdown.
        }
        else cleanupCompleted.TrySetResult();
    }

    /// <summary>Prevents a disposed or faulty diagnostics sink from interrupting resource cleanup.</summary>
    /// <param name="message">One of this owner's fixed messages, never provider, prompt or exception data.</param>
    private void SafeWarning(string message)
    {
        try { logger.LogWarning("{AiRuntimeMessage}", message); }
        catch { /* Diagnostics are borrowed and may already be stopped; cleanup still owns the resources. */ }
    }

    /// <summary>Retains one request context only for its scoped consumer, forwarding the original token unchanged.</summary>
    private sealed class RequestGenerator(AiQueryRuntime owner, HttpContext context) : IStructuredQueryGenerator
    {
        /// <inheritdoc />
        public Task<StructuredQueryProposal?> GenerateAsync(string prompt, QueryVocabulary vocabulary, CancellationToken cancellationToken) =>
            owner.GenerateAsync(context, prompt, vocabulary, cancellationToken);
    }
}
