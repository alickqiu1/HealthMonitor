namespace HealthMonitor.Api.AiQueries;

/// <summary>Reports only an owned local admission reason and bounded retry interval, never request or storage diagnostics.</summary>
internal sealed class AiAdmissionRejectedException : Exception
{
    /// <summary>Distinguishes window exhaustion, daily exhaustion, identity/lifecycle failure and counter failure.</summary>
    internal enum Rejection { RateLimited, BudgetExhausted, AdmissionUnavailable, BudgetUnavailable }

    internal Rejection Reason { get; }
    internal int? RetryAfterSeconds { get; }

    /// <summary>Creates a fixed-message local rejection; HTTP mapping remains a separate API responsibility.</summary>
    /// <param name="reason">One explicitly owned classification.</param>
    /// <param name="retryAfterSeconds">Positive whole seconds for rate/daily exhaustion only, at most one UTC day.</param>
    /// <exception cref="ArgumentOutOfRangeException">Classification or retry metadata violates the owned contract.</exception>
    internal AiAdmissionRejectedException(Rejection reason, int? retryAfterSeconds = null) : base("AI admission was rejected.")
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        bool retryRequired = reason is Rejection.RateLimited or Rejection.BudgetExhausted;
        if (retryRequired ? retryAfterSeconds is null or < 1 or > 86400 : retryAfterSeconds.HasValue)
            throw new ArgumentOutOfRangeException(nameof(retryAfterSeconds));
        Reason = reason; RetryAfterSeconds = retryAfterSeconds;
    }
}
