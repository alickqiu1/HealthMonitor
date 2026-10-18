using System.Text;
using System.Text.Json;
using HealthMonitor.Application.AiQueries;

namespace HealthMonitor.Infrastructure.AiQueries;

/// <summary>Strictly parses a bounded UTF-8 proposal into untrusted Application values.</summary>
/// <remarks>This boundary validates JSON structure, not query semantics or natural-language accuracy. The
/// transport must separately bound bytes before buffering the provider envelope and extracting this proposal.</remarks>
public static class StructuredQueryProposalParser
{
    private const int MaximumBytes = 8192;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] RootFields = ["outcome", "query", "reasonCode"];
    private static readonly string[] QueryFields = ["q", "site", "type", "status", "sort", "direction"];

    /// <summary>Parses one complete proposal without accepting implicit defaults or repairing malformed output.</summary>
    /// <param name="utf8">The proposal bytes, at most 8192 bytes inclusive; not the provider transport envelope.</param>
    /// <returns>Detached immutable candidates that still require Application semantic validation.</returns>
    /// <exception cref="InvalidStructuredQueryException">The bytes, JSON shape, fields, or string types are invalid.</exception>
    public static StructuredQueryProposal Parse(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.IsEmpty || utf8.Length > MaximumBytes) throw new InvalidStructuredQueryException();
        try
        {
            // Some JSON access paths defer string decoding. Never replace malformed UTF-8 with replacement characters.
            _ = StrictUtf8.GetCharCount(utf8.Span);
            using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions
            {
                MaxDepth = 4, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow
            });
            var root = Fields(document.RootElement, RootFields);
            var outcome = String(root["outcome"]);
            var reason = String(root["reasonCode"]);
            DeviceGridQuery? query = null;
            if (root["query"].ValueKind != JsonValueKind.Null)
            {
                var fields = Fields(root["query"], QueryFields);
                query = new(NullableString(fields["q"]), NullableString(fields["site"]),
                    NullableString(fields["type"]), NullableString(fields["status"]),
                    String(fields["sort"]), String(fields["direction"]));
            }
            return new(outcome, query, reason);
        }
        catch (JsonException) { throw new InvalidStructuredQueryException(); }
        catch (DecoderFallbackException) { throw new InvalidStructuredQueryException(); }
        // GetString/property-name decoding rejects escaped unpaired surrogates here, after UTF-8 validation.
        catch (InvalidOperationException) { throw new InvalidStructuredQueryException(); }
    }

    /// <summary>Requires every exact field once, using decoded names so escaped duplicate aliases cannot bypass checks.</summary>
    private static Dictionary<string, JsonElement> Fields(JsonElement element, string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidStructuredQueryException();
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
        {
            var name = field.Name;
            if (!expected.Contains(name, StringComparer.Ordinal) || !fields.TryAdd(name, field.Value))
                throw new InvalidStructuredQueryException();
        }
        if (fields.Count != expected.Length) throw new InvalidStructuredQueryException();
        return fields;
    }

    /// <summary>Decodes required string values; null and numeric enum representations are not strings.</summary>
    private static string String(JsonElement element) => element.ValueKind == JsonValueKind.String
        ? element.GetString()! : throw new InvalidStructuredQueryException();

    /// <summary>Decodes optional filters while retaining explicit null rather than inventing missing-field defaults.</summary>
    private static string? NullableString(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : String(element);
}
