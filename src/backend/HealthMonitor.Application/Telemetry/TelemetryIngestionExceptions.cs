namespace HealthMonitor.Application.Telemetry;

/// <summary>Rejects reuse of a batch identity with different canonical request content.</summary>
/// <remarks>The API may later map this failure to HTTP 409 without exposing persistence exceptions.</remarks>
public sealed class TelemetryIngestionConflictException : InvalidOperationException
{
    /// <summary>Creates the conflict failure without including request content or provider diagnostics.</summary>
    public TelemetryIngestionConflictException()
        : base("BatchId already identifies a different telemetry request.") { }
}

/// <summary>Rejects a new batch containing an unknown or retired registration.</summary>
/// <remarks>The entire batch is rejected before entering the write transaction.</remarks>
public sealed class TelemetryDeviceEligibilityException : ArgumentException
{
    /// <summary>Creates the eligibility failure without treating a partial catalog as an accepted batch.</summary>
    public TelemetryDeviceEligibilityException()
        : base("Every requested device must be registered and active.", "deviceIds") { }
}

/// <summary>Signals bounded contention for the shared ingestion/retention transaction lock.</summary>
/// <remarks>The future HTTP boundary may map this to 503 with Retry-After; unrelated provider errors must not use it.</remarks>
public sealed class TelemetryIngestionBusyException : InvalidOperationException
{
    /// <summary>Creates a sanitized lock-contention failure without provider diagnostics.</summary>
    public TelemetryIngestionBusyException() : base("Telemetry ingestion is busy; retry the same BatchId later.") { }
}
