using HealthMonitor.Application.AiQueries;
using HealthMonitor.Application.AiQueries.Ports;
using static HealthMonitor.Api.AiQueries.AiAdmissionRejectedException;

namespace HealthMonitor.Api.AiQueries;

/// <summary>Reserves local admission and daily budget before exactly one provider attempt through the existing Application port.</summary>
/// <remarks>This unregistered, request-scoped decorator must be consumed through TranslateDeviceQuery, which validates
/// and trims the prompt before this port is reached. It does not duplicate Application's business validator.
/// The supplied HttpContext is scoped, never captured by a singleton; only the owned ingress identity snapshot is read.
/// Missing or unavailable metadata fails closed before any admission lease, persistent reservation or provider call.
/// Policy, counter, inner provider and UTC clock are borrowed resources owned by future composition. The clock must
/// be the same one used by the counter. No database, secret store, forwarding header or HTTP error mapping is accessed.</remarks>
internal sealed class AdmittedStructuredQueryGenerator : IStructuredQueryGenerator
{
    private readonly IStructuredQueryGenerator inner;
    private readonly AiAdmissionPolicy policy;
    private readonly AiDailyBudgetStore budget;
    private readonly HttpContext context;
    private readonly TimeProvider clock;

    /// <summary>Retains borrowed admission and provider dependencies without resolving configuration or performing I/O.</summary>
    /// <param name="inner">The actual provider adapter, never this decorator resolved recursively.</param>
    /// <param name="policy">Shared process admission policy.</param>
    /// <param name="budget">Shared persistent reservation owner.</param>
    /// <param name="context">This request's context after trusted proxy processing.</param>
    /// <param name="clock">The counter's shared UTC clock.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    internal AdmittedStructuredQueryGenerator(IStructuredQueryGenerator inner, AiAdmissionPolicy policy,
        AiDailyBudgetStore budget, HttpContext context, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(inner); ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(budget); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(clock);
        this.inner = inner; this.policy = policy; this.budget = budget; this.context = context; this.clock = clock;
    }

    /// <summary>Holds concurrency until actual provider termination and never refunds a committed daily reservation.</summary>
    /// <param name="prompt">Application-validated trimmed text; the port's existing precondition still applies.</param>
    /// <param name="vocabulary">The same owned immutable vocabulary forwarded unchanged.</param>
    /// <param name="cancellationToken">The same caller token forwarded to admission, persistence and provider.</param>
    /// <returns>The inner untrusted proposal, still subject to Application semantic validation.</returns>
    /// <exception cref="AiAdmissionRejectedException">An owned local control denies the attempt without calling the provider.</exception>
    /// <exception cref="OperationCanceledException">Caller cancellation is observed; an uncooperative inner call must terminate first.</exception>
    /// <remarks>Window tokens consumed by earlier gates are not refunded. Uncancelled provider failures propagate unchanged, with no retry.
    /// A cancellation after persistence but before invocation can consume a daily reservation without reaching the network;
    /// this conservative charge avoids treating uncertain completion as permission for another attempt.</remarks>
    public async Task<StructuredQueryProposal?> GenerateAsync(string prompt, QueryVocabulary vocabulary, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Mutable connection metadata and headers cannot replace the ingress-verified identity or evade its client window.
            var identity = context.Features.Get<AiClientIdentity>();
            var address = identity is { IsVerified: true } ? identity.GetAddress() : null;
            if (address is null) throw new AiAdmissionRejectedException(Rejection.AdmissionUnavailable);
            using var lease = policy.TryEnter(address, cancellationToken);
            switch (lease.Status)
            {
                case AiAdmissionPolicy.Outcome.RateLimited:
                    throw new AiAdmissionRejectedException(Rejection.RateLimited, checked((int)lease.RetryAfter!.Value.TotalSeconds));
                case AiAdmissionPolicy.Outcome.Unavailable:
                    throw new AiAdmissionRejectedException(Rejection.AdmissionUnavailable);
                case AiAdmissionPolicy.Outcome.Acquired: break;
                default: throw new InvalidOperationException("Unsupported AI admission decision.");
            }
            var admissionDay = clock.GetUtcNow().UtcDateTime.Date;
            var reservation = await budget.TryReserveAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            switch (reservation)
            {
                case AiDailyBudgetStore.Outcome.Exhausted:
                    throw new AiAdmissionRejectedException(Rejection.BudgetExhausted, DailyRetryAfter(admissionDay));
                case AiDailyBudgetStore.Outcome.Unavailable:
                    throw new AiAdmissionRejectedException(Rejection.BudgetUnavailable);
                case AiDailyBudgetStore.Outcome.Admitted: break;
                default: throw new InvalidOperationException("Unsupported AI daily budget decision.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Do not abandon a cancelled provider task: releasing its permit early could exceed the two-call bound.
            var proposal = await inner.GenerateAsync(prompt, vocabulary, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return proposal;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Caller abort wins faults racing with reservation/provider completion, after the owned permit has been released.
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    /// <summary>Rounds to the next UTC midnight; a day transition during reservation permits a prompt retry instead of an extra day's delay.</summary>
    private int DailyRetryAfter(DateTime admissionDay)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (now.Date != admissionDay) return 1;
        return Math.Clamp(checked((int)Math.Ceiling((admissionDay.AddDays(1) - now).TotalSeconds)), 1, 86400);
    }
}
