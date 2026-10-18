using Microsoft.AspNetCore.Mvc;

namespace HealthMonitor.Api.Http;

/// <summary>Creates fixed simulator failures without reflecting rejected bodies, credentials, or dependency diagnostics.</summary>
internal static class SimulationProblems
{
    /// <summary>Creates the same stable problem shape for middleware, resource filters, and controller errors.</summary>
    /// <param name="context">Correlation identifier only.</param>
    /// <param name="status">Owned HTTP status.</param>
    /// <param name="code">Owned stable code, never a request value.</param>
    /// <param name="title">Fixed safe description.</param>
    /// <returns>ProblemDetails without provider data.</returns>
    internal static ProblemDetails Create(HttpContext context, int status, string code, string title)
    {
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code; problem.Extensions["traceId"] = context.TraceIdentifier;
        return problem;
    }

    /// <summary>Writes a middleware rejection before controller binding or ingestion can execute.</summary>
    /// <param name="context">Request cancellation, response, and correlation identifier.</param>
    /// <param name="status">Owned response status.</param>
    /// <param name="code">Fixed code selected by server code.</param>
    /// <param name="title">Fixed public description without input or diagnostics.</param>
    /// <returns>Completion after writing the problem response.</returns>
    internal static Task WriteAsync(HttpContext context, int status, string code, string title)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(Create(context, status, code, title), options: null,
            contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }

    /// <summary>Returns an MVC rejection using the identical safe content type and status.</summary>
    /// <param name="context">Correlation identifier only.</param>
    /// <param name="status">Owned response status.</param>
    /// <param name="code">Fixed code selected by server code.</param>
    /// <param name="title">Fixed public description without input or diagnostics.</param>
    /// <returns>An MVC result with application/problem+json content.</returns>
    internal static ObjectResult Result(HttpContext context, int status, string code, string title)
    {
        var result = new ObjectResult(Create(context, status, code, title)) { StatusCode = status };
        result.ContentTypes.Add("application/problem+json"); return result;
    }
}
