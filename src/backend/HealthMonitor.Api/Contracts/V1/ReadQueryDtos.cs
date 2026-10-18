using System.ComponentModel.DataAnnotations;

namespace HealthMonitor.Api.Contracts.V1;

/// <summary>Shared shape validation for one-based V1 read pages; policy also validates at the composition boundary.</summary>
public class PagingReadQueryDto
{
    /// <summary>One-based requested page, default one.</summary>
    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;
    /// <summary>Requested capacity from one through 100, default 25.</summary>
    [Range(1, 100)]
    public int PageSize { get; set; } = 25;
}

/// <summary>Raw grid tokens; pure DeviceGridQuery owns normalization and allowlists before module access.</summary>
/// <remarks>Preserve supplied blank tokens so MVC cannot turn invalid filters into omitted defaults. Search alone treats trimmed emptiness as neutral.</remarks>
public sealed class DeviceGridQueryDto : PagingReadQueryDto
{
    /// <summary>Literal search, trimmed before the 100-unit limit.</summary>
    public string? Q { get; set; }
    /// <summary>Optional supported site token, including a supplied invalid blank.</summary>
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Site { get; set; }
    /// <summary>Optional supported category token.</summary>
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Type { get; set; }
    /// <summary>Optional canonical health token.</summary>
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Status { get; set; }
    /// <summary>Optional explicit sort key; absence alone selects the default.</summary>
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Sort { get; set; }
    /// <summary>Optional asc/desc; absence alone selects ascending.</summary>
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Direction { get; set; }
}

/// <summary>Raw telemetry range endpoints; parsing requires an explicit zero-offset ISO8601 zone.</summary>
public sealed class TelemetryHistoryQueryDto : PagingReadQueryDto
{
    /// <summary>Optional inclusive endpoint; supplied blanks are invalid.</summary>
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? FromUtc { get; set; }
    /// <summary>Optional exclusive endpoint; supplied blanks are invalid.</summary>
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? ToUtc { get; set; }
}

/// <summary>Incident history accepts lifecycle membership, deliberately no date window.</summary>
public sealed class IncidentHistoryQueryDto : PagingReadQueryDto
{
    /// <summary>all, open, or resolved; omitted means all.</summary>
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? State { get; set; }
}
