using System.Globalization;
using Microsoft.AspNetCore.Mvc;

namespace HealthMonitor.Api.Http;

/// <summary>Creates owned validation responses without reflecting raw query values or model-binding exception messages.</summary>
internal static class ReadRequestErrors
{
    /// <summary>Rejects unknown and duplicate keys, including casing variants, before application query ports are called.</summary>
    /// <param name="context">Current request.</param>
    /// <param name="allowed">Explicit names supported by this route.</param>
    /// <returns>An owned error result, or null for valid unique keys.</returns>
    internal static BadRequestObjectResult? ValidateKeys(HttpContext context, params string[] allowed)
    {
        var names = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in context.Request.Query)
            if (!names.Contains(entry.Key) || !seen.Add(entry.Key) || entry.Value.Count != 1)
                return Invalid(context, "query");
        return null;
    }

    /// <summary>Creates a generic field-oriented 400 without echoing rejected values.</summary>
    /// <param name="context">Current request for correlation only.</param>
    /// <param name="field">Owned field name selected by application code, never a raw unknown query key.</param>
    /// <returns>Validation ProblemDetails with a safe correlation identifier.</returns>
    internal static BadRequestObjectResult Invalid(HttpContext context, string field)
    {
        var problem = new ValidationProblemDetails(new Dictionary<string, string[]> { [field] = new[] { "A supported value is required." } })
        { Status = StatusCodes.Status400BadRequest, Title = "Invalid request." };
        problem.Extensions["code"] = "validation_failed";
        problem.Extensions["traceId"] = context.TraceIdentifier;
        var result = new BadRequestObjectResult(problem);
        result.ContentTypes.Add("application/problem+json");
        return result;
    }

    /// <summary>Sanitizes automatic binding/annotation failures too; default MVC errors can contain rejected input.</summary>
    /// <param name="context">Invalid model state, whose raw values and error messages are intentionally ignored.</param>
    /// <returns>Owned generic request error.</returns>
    internal static IActionResult InvalidModelState(ActionContext context) => Invalid(context.HttpContext, "request");

    /// <summary>Parses only explicit-zone ISO8601 UTC endpoints, avoiding local timezone assumptions.</summary>
    /// <param name="text">Raw optional token; absence alone is null.</param>
    /// <param name="value">Parsed zero-offset value, or null when omitted.</param>
    /// <returns>False for blank, timezone-less, nonzero-offset, or malformed values.</returns>
    internal static bool TryUtc(string? text, out DateTimeOffset? value)
    {
        value = null;
        if (text is null) return true;
        if (text.Length > 33 || !(text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal))) return false;
        string[] formats = { "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz" };
        if (!DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
            out var parsed) || parsed.Offset != TimeSpan.Zero) return false;
        value = parsed;
        return true;
    }
}
