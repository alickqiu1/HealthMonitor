using HealthMonitor.Api.Composition;
using HealthMonitor.Api.Contracts.V1;
using HealthMonitor.Api.Http;
using HealthMonitor.Api.Simulation;
using HealthMonitor.Application.Telemetry;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HealthMonitor.Api.Controllers;

/// <summary>Exposes the explicitly enabled local synthetic ingestion boundary; disabled mode removes this controller from discovery.</summary>
/// <param name="ingestion">Existing Application transaction/replay boundary.</param>
[ApiController]
[Route("api/v1/simulation")]
[EnableCors(SimulationApiComposition.CorsPolicyName)]
[EnableRateLimiting(SimulationApiComposition.RatePolicyName)]
[ServiceFilter(typeof(SimulationBodyLimitFilter))]
public sealed class SimulationController(ITelemetryIngestionService ingestion) : ControllerBase
{
    /// <summary>Returns 200 only after the Application boundary acknowledges a commit or retrieves its saved receipt.</summary>
    /// <param name="request">Strict synthetic request DTO.</param>
    /// <param name="token">Request cancellation, including during lock acquisition or persistence.</param>
    /// <returns>Committed result, owned validation/conflict/availability failure, or sanitized unexpected failure.</returns>
    [HttpPost("batches")]
    [RequestSizeLimit(SimulationBodyLimitFilter.MaximumBytes)]
    [ProducesResponseType<SimulationBatchResultDto>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(413)]
    [ProducesResponseType<ProblemDetails>(415)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    [ProducesResponseType<ProblemDetails>(500)]
    public async Task<IActionResult> Create([FromBody] SimulationBatchRequestDto request, CancellationToken token)
    {
        if (ReadRequestErrors.ValidateKeys(HttpContext) is { } queryError) return queryError;
        var input = new TelemetryIngestionRequest(request.BatchId!.Value, request.Scenario!, Array.AsReadOnly(request.DeviceIds!.ToArray()));
        try
        {
            // Reuse the pure bounded shape rules; Application independently computes the authoritative receipt hash.
            _ = TelemetryRequestCanonicalizer.ComputeHash(input);
            string id = input.BatchId.ToString("N");
            if (id[12] != '7' || id[16] is not ('8' or '9' or 'a' or 'b')) return ReadRequestErrors.Invalid(HttpContext, "batchId");
        }
        catch (ArgumentException) { return ReadRequestErrors.Invalid(HttpContext, "request"); }

        TelemetryIngestionResult result;
        try { result = await ingestion.IngestAsync(input, token); }
        catch (TelemetryIngestionConflictException) { return SimulationProblems.Result(HttpContext, 409, "batch_id_conflict", "Batch identity conflicts with its saved request."); }
        catch (TelemetryDeviceEligibilityException) { return SimulationProblems.Result(HttpContext, 400, "device_ineligible", "Selected devices must be registered and active."); }
        catch (TelemetryBatchIdValidationException error) when (error.Code is "batch_id_invalid" or "batch_id_expired" or "batch_id_future")
        { return SimulationProblems.Result(HttpContext, 400, error.Code, "Batch identity is invalid for a new request."); }
        catch (TelemetryIngestionBusyException)
        { Response.Headers.RetryAfter = "2"; return SimulationProblems.Result(HttpContext, 503, "simulation_busy", "Simulation is busy; retry the same batch identity."); }
        // Freshness and catalog eligibility remain behind receipt-first replay in Application. Never retry a write with a new ID here.
        if (result is null || result.BatchId != input.BatchId || result.AffectedDeviceCount != input.DeviceIds.Count
            || result.CommittedAtUtc == default || result.CommittedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Invalid committed simulation result.");
        return Ok(new SimulationBatchResultDto(result.BatchId, result.AffectedDeviceCount, result.CommittedAtUtc.UtcDateTime, result.Replayed));
    }
}
