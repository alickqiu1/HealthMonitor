using System.Text.Json.Serialization;
using HealthMonitor.Api.Http;
using HealthMonitor.Domain.Telemetry;

namespace HealthMonitor.Api.Composition;

/// <summary>Shares the production read-feature binding, JSON, and sanitized error pipeline with isolated HTTP tests.</summary>
internal static class ReadApiComposition
{
    /// <summary>Adds controllers and canonical V1 JSON without changing database selection or starting workers.</summary>
    /// <param name="services">Application services with read composers registered separately.</param>
    /// <returns>The same collection.</returns>
    /// <exception cref="ArgumentNullException">Services is null.</exception>
    internal static IServiceCollection AddReadApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddControllers().AddApplicationPart(typeof(HealthMonitor.Api.Controllers.DevicesController).Assembly).AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter<HealthState>(allowIntegerValues: false));
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter<PingOutcome>(allowIntegerValues: false));
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter<IncidentKind>(allowIntegerValues: false));
        }).ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = ReadRequestErrors.InvalidModelState);
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
            context.ProblemDetails.Extensions.TryAdd("code", context.ProblemDetails.Status == 404 ? "not_found" : "request_failed");
        });
        // Provider diagnostics may include SQL, connection metadata, exception messages, and stacks even before our handler runs.
        // Keep owned type/trace diagnostics instead. Post-configuration prevents a more-specific provider rule from bypassing this boundary.
        services.PostConfigure<LoggerFilterOptions>(options =>
        {
            string[] blocked = { "Microsoft.EntityFrameworkCore", "Npgsql" };
            // Provider specificity outranks category specificity. Mirror every configured provider name as well as neutral rules.
            var providers = options.Rules.Select(rule => rule.ProviderName).Prepend(null).Distinct(StringComparer.Ordinal).ToArray();
            for (int index = options.Rules.Count - 1; index >= 0; index--)
                if (options.Rules[index].CategoryName is { } category && blocked.Any(prefix => category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    options.Rules.RemoveAt(index);
            foreach (var provider in providers)
                foreach (var prefix in blocked) options.Rules.Add(new LoggerFilterRule(provider, prefix, LogLevel.None, null));
        });
        services.AddExceptionHandler<ReadExceptionHandler>();
        return services;
    }

    /// <summary>Installs the same owned exception boundary in production and test feature hosts.</summary>
    /// <param name="app">Configured application pipeline.</param>
    /// <returns>The same pipeline.</returns>
    /// <remarks>Handled failures use only the owned type/trace log. Suppress framework exception diagnostics that could log raw provider messages.</remarks>
    internal static IApplicationBuilder UseReadApiErrors(this IApplicationBuilder app)
    {
        app.UseExceptionHandler(new ExceptionHandlerOptions { SuppressDiagnosticsCallback = _ => true });
        // Routes absent from MVC still use the uniform contract; never echo their raw path/query into the body.
        return app.UseStatusCodePages(async context =>
        {
            var http = context.HttpContext;
            var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = http.Response.StatusCode, Title = "Request could not be completed."
            };
            problem.Extensions["code"] = http.Response.StatusCode == 404 ? "not_found" : "request_failed";
            problem.Extensions["traceId"] = http.TraceIdentifier;
            await http.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken: http.RequestAborted);
        });
    }
}
