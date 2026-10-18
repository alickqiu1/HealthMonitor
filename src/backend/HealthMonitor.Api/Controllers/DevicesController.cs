using HealthMonitor.Api.Contracts.V1;
using HealthMonitor.Api.Fleet;
using HealthMonitor.Api.Http;
using HealthMonitor.Application.Telemetry;
using Microsoft.AspNetCore.Mvc;

namespace HealthMonitor.Api.Controllers;

/// <summary>Exposes V1 inventory, computed health, and server-paged history through module-owned query boundaries.</summary>
/// <param name="fleet">Complete bounded active-fleet composer.</param>
/// <param name="detail">Explicit active/retired detail composer.</param>
/// <param name="history">Validated server-paged history composer.</param>
[ApiController]
[Route("api/v1/devices")]
public sealed class DevicesController(FleetReadService fleet, DeviceDetailReadService detail, DeviceHistoryReadService history) : ControllerBase
{
    /// <summary>Returns globally filtered/sorted active inventory, then the requested page.</summary>
    /// <param name="query">Raw allowlisted query shape.</param>
    /// <param name="token">Request cancellation.</param>
    /// <returns>200 with true filtered count, or a sanitized 400 for invalid input.</returns>
    [ProducesResponseType<PagedResult<DeviceRowDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DeviceGridQueryDto query, CancellationToken token)
    {
        if (ReadRequestErrors.ValidateKeys(HttpContext, "page", "pageSize", "q", "site", "type", "status", "sort", "direction") is { } invalid) return invalid;
        DeviceGridQuery validated;
        try { validated = new(query.Page, query.PageSize, query.Q, query.Site, query.Type, query.Status, query.Sort, query.Direction); }
        catch (ArgumentException error) { return ReadRequestErrors.Invalid(HttpContext, error.ParamName ?? "query"); }
        // Catch only input construction above. Provider/domain validation failures during reads remain server errors.
        var page = DeviceGridPaging.Apply(await fleet.ReadAsync(token), validated);
        return Ok(new PagedResult<DeviceRowDto>(page.Items.Select(Row).ToArray(), page.Page, page.PageSize, page.TotalCount, page.AsOfUtc.UtcDateTime));
    }

    /// <summary>Returns active or retired detail; malformed UUID binding is 400 and unknown valid UUID is 404.</summary>
    /// <param name="deviceId">Explicit catalog identity.</param>
    /// <param name="token">Request cancellation.</param>
    /// <returns>200 detail or 404 for unknown inventory.</returns>
    [ProducesResponseType<DeviceDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    [HttpGet("{deviceId}")]
    public async Task<IActionResult> GetDetail(Guid deviceId, CancellationToken token)
    {
        if (ReadRequestErrors.ValidateKeys(HttpContext) is { } invalid) return invalid;
        var result = await detail.ReadAsync(deviceId, token);
        if (result is null) return NotFound();
        var row = result.Row;
        return Ok(new DeviceDetailDto(row.DeviceId, row.AssetTag, row.DisplayName, row.DeviceType, row.SiteCode, row.Status,
            row.LastObservedAtUtc?.UtcDateTime, row.LatencyMs, row.CpuPercent, result.CreatedAtUtc.UtcDateTime,
            result.RetiredAtUtc?.UtcDateTime, result.LastSuccessfulAtUtc?.UtcDateTime, result.AsOfUtc.UtcDateTime));
    }

    /// <summary>Returns newest telemetry in a validated half-open UTC window, including empty known-device history.</summary>
    /// <param name="deviceId">Explicit active or retired identity.</param>
    /// <param name="query">Page and optional explicit-zone UTC endpoints.</param>
    /// <param name="token">Request cancellation.</param>
    /// <returns>200 page, 400 invalid query, or 404 unknown inventory.</returns>
    [ProducesResponseType<PagedResult<TelemetryPingDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    [HttpGet("{deviceId}/telemetry")]
    public async Task<IActionResult> GetTelemetry(Guid deviceId, [FromQuery] TelemetryHistoryQueryDto query, CancellationToken token)
    {
        if (ReadRequestErrors.ValidateKeys(HttpContext, "page", "pageSize", "fromUtc", "toUtc") is { } invalid) return invalid;
        if (!ReadRequestErrors.TryUtc(query.FromUtc, out var from)) return ReadRequestErrors.Invalid(HttpContext, "fromUtc");
        if (!ReadRequestErrors.TryUtc(query.ToUtc, out var to)) return ReadRequestErrors.Invalid(HttpContext, "toUtc");
        var result = await history.ReadTelemetryAsync(deviceId, query.Page, query.PageSize, from, to, token);
        if (result is null) return NotFound();
        var page = result.Page;
        return Ok(new PagedResult<TelemetryPingDto>(page.Items.Select(p => new TelemetryPingDto(p.Id, p.DeviceId,
            p.ObservedAtUtc.UtcDateTime, p.Outcome, p.LatencyMs, p.CpuPercent)).ToArray(), page.Paging.Page,
            page.Paging.PageSize, page.TotalCount, result.AsOfUtc.UtcDateTime));
    }

    /// <summary>Returns incidents by opening time/UUID descending, with only lifecycle membership filtering.</summary>
    /// <param name="deviceId">Explicit active or retired identity.</param>
    /// <param name="query">Page and all/open/resolved token.</param>
    /// <param name="token">Request cancellation.</param>
    /// <returns>200 page, 400 invalid query, or 404 unknown inventory.</returns>
    [ProducesResponseType<PagedResult<IncidentDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    [HttpGet("{deviceId}/incidents")]
    public async Task<IActionResult> GetIncidents(Guid deviceId, [FromQuery] IncidentHistoryQueryDto query, CancellationToken token)
    {
        if (ReadRequestErrors.ValidateKeys(HttpContext, "page", "pageSize", "state") is { } invalid) return invalid;
        var state = query.State?.Trim() switch
        {
            null or "all" => IncidentHistoryState.All,
            "open" => IncidentHistoryState.Open,
            "resolved" => IncidentHistoryState.Resolved,
            _ => (IncidentHistoryState)(-1)
        };
        if (!Enum.IsDefined(state)) return ReadRequestErrors.Invalid(HttpContext, "state");
        var result = await history.ReadIncidentsAsync(deviceId, query.Page, query.PageSize, state, token);
        if (result is null) return NotFound();
        var page = result.Page;
        return Ok(new PagedResult<IncidentDto>(page.Items.Select(i => new IncidentDto(i.Id, i.DeviceId,
            i.Kind, i.OpenedAtUtc.UtcDateTime, i.ResolvedAtUtc?.UtcDateTime, i.Summary)).ToArray(), page.Paging.Page,
            page.Paging.PageSize, page.TotalCount, result.AsOfUtc.UtcDateTime));
    }

    /// <summary>Maps a read-only internal row without exposing internal metadata or DateTimeOffset +00:00 formatting.</summary>
    /// <param name="row">Immutable composed inventory and computed health.</param>
    /// <returns>Exact V1 row with UTC DateTime values for Z serialization.</returns>
    private static DeviceRowDto Row(FleetRow row) => new(row.DeviceId, row.AssetTag, row.DisplayName, row.DeviceType, row.SiteCode,
        row.Status, row.LastObservedAtUtc?.UtcDateTime, row.LatencyMs, row.CpuPercent);
}
