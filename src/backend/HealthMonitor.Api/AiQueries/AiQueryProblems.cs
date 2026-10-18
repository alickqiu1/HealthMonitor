using Microsoft.AspNetCore.Mvc;

namespace HealthMonitor.Api.AiQueries;

/// <summary>Builds contextual AI errors from owned constants without including prompts or provider diagnostics.</summary>
internal static class AiQueryProblems
{
    /// <summary>Returns a safe failure with the standard status/code/trace shape.</summary>
    /// <param name="context">Request supplying only the correlation identifier.</param>
    /// <param name="status">Owned HTTP status.</param>
    /// <param name="code">Owned machine-readable code.</param>
    /// <param name="title">Fixed plain-text explanation.</param>
    /// <returns>Problem JSON; endpoint middleware supplies no-store even if an exception clears the response.</returns>
    internal static ObjectResult Result(HttpContext context, int status, string code, string title)
    {
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = context.TraceIdentifier;
        var result = new ObjectResult(problem) { StatusCode = status };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }
}
