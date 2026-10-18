using System.Text.Json;
using HealthMonitor.Api.Http;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HealthMonitor.Api.Simulation;

/// <summary>Bounds actual request bytes before MVC binding and rejects ambiguous/unknown JSON fields.</summary>
/// <remarks>Content-Length alone is insufficient for streamed bodies. MVC still owns DTO binding and annotation validation.</remarks>
internal sealed class SimulationBodyLimitFilter : IAsyncResourceFilter
{
    internal const int MaximumBytes = 16 * 1024;

    /// <summary>Reads at most the limit plus one byte, then supplies a bounded rewindable body to MVC.</summary>
    /// <param name="context">Resource stage, reached only after local access and the rate limiter.</param>
    /// <param name="next">Remaining MVC binding/action pipeline.</param>
    /// <returns>Completion after a safe rejection or the complete downstream pipeline.</returns>
    /// <exception cref="OperationCanceledException">Request reading or downstream work was canceled.</exception>
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!request.HasJsonContentType())
        {
            context.Result = SimulationProblems.Result(context.HttpContext, 415, "unsupported_media_type", "JSON is required."); return;
        }
        if (request.ContentLength > MaximumBytes) { TooLarge(); return; }
        await using var bounded = new MemoryStream(MaximumBytes + 1);
        byte[] bytes = new byte[4096];
        try
        {
            while (bounded.Length <= MaximumBytes)
            {
                int read = await request.Body.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, MaximumBytes + 1 - (int)bounded.Length)), context.HttpContext.RequestAborted);
                if (read == 0) break;
                await bounded.WriteAsync(bytes.AsMemory(0, read), context.HttpContext.RequestAborted);
            }
        }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { TooLarge(); return; }
        if (bounded.Length > MaximumBytes) { TooLarge(); return; }
        bounded.Position = 0;
        try
        {
            using var document = await JsonDocument.ParseAsync(bounded, new JsonDocumentOptions { MaxDepth = 64 }, context.HttpContext.RequestAborted);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Any(field =>
                field.Name is not ("batchId" or "deviceIds" or "scenario") || !seen.Add(field.Name)))
            { context.Result = ReadRequestErrors.Invalid(context.HttpContext, "request"); return; }
        }
        catch (JsonException) { context.Result = ReadRequestErrors.Invalid(context.HttpContext, "request"); return; }
        // Inspection rejects ambiguous fields; rewind for the standard MVC formatter rather than introducing a custom binder.
        bounded.Position = 0;
        var original = request.Body;
        try { request.Body = bounded; await next(); }
        finally { request.Body = original; }

        void TooLarge() => context.Result = SimulationProblems.Result(context.HttpContext, 413, "request_too_large", "Request body is too large.");
    }
}
