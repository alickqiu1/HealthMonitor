using HealthMonitor.Application.Device;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace HealthMonitor.Api.Http;

/// <summary>Maps owned validation/capacity errors and sanitizes unexpected read failures before public routes are introduced.</summary>
/// <remarks>This minimum boundary precedes the remaining Step 4.3 operational/security work. No exception object/message is logged.</remarks>
/// <param name="logger">Owned diagnostics sink; exception objects and raw messages are excluded.</param>
internal sealed class ReadExceptionHandler(ILogger<ReadExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>Handles failures without exposing provider details; unrelated dependency cancellation is still a server failure.</summary>
    /// <param name="context">Current request/response.</param>
    /// <param name="exception">Failure whose type, not raw message, determines the response.</param>
    /// <param name="cancellationToken">Response cancellation.</param>
    /// <returns>True when handled by this boundary.</returns>
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            return true;
        }
        int status = exception switch
        {
            InvalidTelemetryRangeException => StatusCodes.Status400BadRequest,
            DeviceCatalogCapacityExceededException => StatusCodes.Status503ServiceUnavailable,
            Npgsql.NpgsqlException database when IsUnavailable(database) => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError
        };
        if (status >= 500)
            logger.LogError("Read request failed. ErrorType={ErrorType}; TraceId={TraceId}",
                exception.GetType().Name, context.TraceIdentifier);
        var problem = new ProblemDetails { Status = status, Title = status switch
        {
            400 => "Invalid telemetry range.",
            503 => "Service temporarily unavailable.",
            _ => "An unexpected error occurred."
        }};
        problem.Extensions["code"] = exception switch
        {
            InvalidTelemetryRangeException => "validation_failed",
            DeviceCatalogCapacityExceededException => "fleet_capacity_exceeded",
            Npgsql.NpgsqlException database when IsUnavailable(database) => "database_unavailable",
            _ => "unexpected_error"
        };
        problem.Extensions["traceId"] = context.TraceIdentifier;
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken: cancellationToken);
        return true;
    }
    /// <summary>Separates transport/server availability from unexpected PostgreSQL query, schema, or data errors.</summary>
    /// <param name="error">Structured provider failure; raw messages are never inspected or reflected.</param>
    /// <returns>True for connection-class/server-unavailable states or transient non-PostgreSQL transport failures.</returns>
    /// <remarks>Do not classify every provider error or retryable SQL transaction error as an outage.</remarks>
    private static bool IsUnavailable(Npgsql.NpgsqlException error) => error is Npgsql.PostgresException postgres
        ? postgres.SqlState.StartsWith("08", StringComparison.Ordinal) || postgres.SqlState is
            Npgsql.PostgresErrorCodes.TooManyConnections or Npgsql.PostgresErrorCodes.AdminShutdown or
            Npgsql.PostgresErrorCodes.CrashShutdown or Npgsql.PostgresErrorCodes.CannotConnectNow
        : error.IsTransient;
}
