using HealthMonitor.Api.Contracts.V1;
using HealthMonitor.Api.Fleet;
using HealthMonitor.Api.Http;
using Microsoft.AspNetCore.Mvc;

namespace HealthMonitor.Api.Controllers;

/// <summary>Exposes global active-fleet metrics without accepting device-grid filters.</summary>
/// <param name="fleet">Complete bounded active-fleet composer.</param>
[ApiController]
[Route("api/v1/metrics")]
public sealed class MetricsController(FleetReadService fleet) : ControllerBase
{
    /// <summary>Returns complete active-fleet counts and fractional fresh-success latency.</summary>
    /// <param name="token">Request cancellation.</param>
    /// <returns>200 global metrics; unsupported query keys are 400.</returns>
    [ProducesResponseType<MetricsSummaryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken token)
    {
        if (ReadRequestErrors.ValidateKeys(HttpContext) is { } invalid) return invalid;
        var snapshot = await fleet.ReadAsync(token);
        var m = snapshot.Metrics;
        return Ok(new MetricsSummaryDto(m.Total, m.Healthy, m.Degraded, m.Offline, m.Unknown,
            m.AverageLatencyMs, m.SampleCount, snapshot.AsOfUtc.UtcDateTime));
    }
}
