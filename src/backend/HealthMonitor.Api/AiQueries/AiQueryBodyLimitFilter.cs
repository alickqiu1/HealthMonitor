using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Net.Http.Headers;

namespace HealthMonitor.Api.AiQueries;

/// <summary>Requires a single prompt string in bounded strict UTF-8 JSON before model binding or generation.</summary>
/// <remarks>Actual bytes are bounded for streamed requests too. The 8 KB transport limit is separate from
/// Application's 500 UTF-16-unit prompt limit; neither substitutes for provider admission controls.</remarks>
internal sealed class AiQueryBodyLimitFilter : IAsyncResourceFilter
{
    internal const int MaximumBytes = 8192;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Rejects unsupported input and replays only the bounded inspected bytes to the standard MVC formatter.</summary>
    /// <param name="context">The selected AI endpoint request.</param>
    /// <param name="next">Remaining model binding and controller execution.</param>
    /// <returns>Rejection or completion of the downstream request.</returns>
    /// <exception cref="OperationCanceledException">Request reading or downstream execution is cancelled.</exception>
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (request.Query.Count != 0) { Invalid(); return; }
        if (!IsUtf8Json(request))
        { context.Result = AiQueryProblems.Result(context.HttpContext, 415, "unsupported_media_type", "UTF-8 JSON content is required."); return; }
        if (request.ContentLength > MaximumBytes) { TooLarge(); return; }
        using var bounded = new MemoryStream();
        var bytes = new byte[MaximumBytes + 1];
        try
        {
            while (bounded.Length <= MaximumBytes)
            {
                int read = await request.Body.ReadAsync(bytes.AsMemory(0, bytes.Length - (int)bounded.Length), request.HttpContext.RequestAborted);
                if (read == 0) break;
                await bounded.WriteAsync(bytes.AsMemory(0, read), request.HttpContext.RequestAborted);
            }
        }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { TooLarge(); return; }
        if (bounded.Length > MaximumBytes) { TooLarge(); return; }
        try
        {
            var payload = bounded.GetBuffer().AsMemory(0, (int)bounded.Length);
            _ = StrictUtf8.GetCharCount(payload.Span);
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) { Invalid(); return; }
            var fields = document.RootElement.EnumerateObject().ToArray();
            if (fields.Length != 1 || fields[0].Name != "prompt" || fields[0].Value.ValueKind != JsonValueKind.String)
            { Invalid(); return; }
            _ = fields[0].Value.GetString(); // Force escaped-surrogate validation before MVC can decode differently.
        }
        catch (JsonException) { Invalid(); return; }
        catch (DecoderFallbackException) { Invalid(); return; }
        catch (InvalidOperationException) { Invalid(); return; }
        bounded.Position = 0;
        var original = request.Body;
        try { request.Body = bounded; await next(); }
        finally { request.Body = original; }

        void Invalid() => context.Result = AiQueryProblems.Result(context.HttpContext, 400, "ai_prompt_invalid", "The prompt request is invalid.");
        void TooLarge() => context.Result = AiQueryProblems.Result(context.HttpContext, 413, "request_too_large", "Request body is too large.");
    }

    /// <summary>Requires MVC to decode exactly the UTF-8 bytes inspected here; repeated charset parameters are ambiguous.</summary>
    /// <param name="request">The declared request media type, without reading or logging its contents.</param>
    /// <returns>True for JSON or +json with absent charset or a single valid UTF-8 charset.</returns>
    private static bool IsUtf8Json(HttpRequest request)
    {
        if (!request.HasJsonContentType() || !MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)) return false;
        var charsets = mediaType.Parameters.Where(parameter =>
            string.Equals(parameter.Name.Value, "charset", StringComparison.OrdinalIgnoreCase)).ToArray();
        return charsets.Length == 0 || (charsets.Length == 1
            && string.Equals(HeaderUtilities.RemoveQuotes(charsets[0].Value).Value, "utf-8", StringComparison.OrdinalIgnoreCase));
    }
}
