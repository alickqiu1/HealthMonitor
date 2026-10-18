using HealthMonitor.Api.Health;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace HealthMonitor.Api.Composition;

/// <summary>Composes minimal liveness and sanitized database readiness using the framework health-check service.</summary>
internal static class HealthProbeComposition
{
    internal const string DatabaseCheckName = "database";

    /// <summary>Registers the check lazily on the existing DI-owned pool; no connection or additional pool is created here.</summary>
    /// <param name="services">Runtime services with NpgsqlDataSource already registered.</param>
    /// <returns>The same collection.</returns>
    internal static IServiceCollection AddOperationalProbes(this IServiceCollection services)
    {
        services.AddSingleton(provider => new DatabaseReadinessCheck(provider.GetRequiredService<NpgsqlDataSource>()));
        // No IHealthCheckPublisher is registered, so the framework's publisher host schedules no background checks.
        services.AddHealthChecks().AddCheck<DatabaseReadinessCheck>(DatabaseCheckName,
            failureStatus: HealthStatus.Unhealthy, timeout: TimeSpan.FromSeconds(5));
        return services;
    }

    /// <summary>Maps public GET probes; liveness does not resolve the check, health service, or runtime pool.</summary>
    /// <param name="endpoints">Application routing after CORS and authorization middleware.</param>
    /// <returns>The same route builder.</returns>
    internal static IEndpointRouteBuilder MapOperationalProbes(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { status = "alive" });
        });
        endpoints.MapGet("/health/ready", WriteReadinessAsync);
        return endpoints;
    }

    /// <summary>Lets the framework execute/timeout the check while owning safe public responses and service/factory failure handling.</summary>
    /// <param name="context">Request cancellation and correlation identifier.</param>
    /// <param name="logs">Owned diagnostic logger, excluding raw exceptions and report data.</param>
    /// <returns>Completion after a minimal 200 response or sanitized 503 ProblemDetails.</returns>
    /// <exception cref="OperationCanceledException">The client aborted; do not convert it into an availability response.</exception>
    private static async Task WriteReadinessAsync(HttpContext context, ILoggerFactory logs)
    {
        context.Response.Headers.CacheControl = "no-store";
        bool ready = false;
        string errorType = "Unhealthy";
        try
        {
            // Resolve inside the guard: service/registration construction failures must also become safe readiness failures.
            var checks = context.RequestServices.GetRequiredService<HealthCheckService>();
            var report = await checks.CheckHealthAsync(check => check.Name == DatabaseCheckName, context.RequestAborted);
            // An empty filtered report is aggregate Healthy; require evidence that the database check actually ran.
            ready = report.Status == HealthStatus.Healthy
                && report.Entries.TryGetValue(DatabaseCheckName, out var database) && database.Status == HealthStatus.Healthy;
        }
        catch (Exception error)
        {
            if (context.RequestAborted.IsCancellationRequested) throw new OperationCanceledException(context.RequestAborted);
            errorType = error.GetType().Name;
        }
        if (ready)
        {
            await context.Response.WriteAsJsonAsync(new { status = "ready" }, context.RequestAborted);
            return;
        }
        // Framework health-check logs are filtered centrally; emit only this owned, correlated failure diagnostic.
        logs.CreateLogger(typeof(HealthProbeComposition).FullName!).LogWarning(
            "Readiness unavailable. ErrorType={ErrorType}; TraceId={TraceId}", errorType, context.TraceIdentifier);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        var problem = new ProblemDetails { Status = 503, Title = "Service temporarily unavailable." };
        problem.Extensions["code"] = "database_unavailable";
        problem.Extensions["traceId"] = context.TraceIdentifier;
        await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}
