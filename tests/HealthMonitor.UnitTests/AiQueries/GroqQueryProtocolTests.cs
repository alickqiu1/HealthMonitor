using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HealthMonitor.Application.AiQueries;
using HealthMonitor.Application.AiQueries.Ports;
using HealthMonitor.Infrastructure.AiQueries;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Verifies provider bytes and schema without constructing an HTTP client, reading a key, or evaluating a live model.</summary>
public sealed class GroqQueryProtocolTests
{
    private const string Canary = "private-protocol-canary";
    private const string Proposal = """{"outcome":"Ready","query":{"q":null,"site":null,"type":null,"status":null,"sort":"displayName","direction":"asc"},"reasonCode":"ready"}""";
    private static readonly QueryVocabulary Vocabulary = new(["DEN"], ["Server", "SCADA"]);

    [Fact]
    public void Request_IsFixedBoundedAndSeparatesUserTextFromInstructions()
    {
        using var document = JsonDocument.Parse(GroqQueryProtocol.CreateRequestBody(Canary, Vocabulary));
        var root = document.RootElement;
        Assert.Equal("https://api.groq.com/openai/v1/chat/completions", GroqQueryProtocol.Endpoint.AbsoluteUri);
        Assert.Equal("openai/gpt-oss-20b", root.GetProperty("model").GetString());
        Assert.Equal(400, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal(1, root.GetProperty("n").GetInt32()); Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("low", root.GetProperty("reasoning_effort").GetString()); Assert.False(root.GetProperty("include_reasoning").GetBoolean());
        foreach (var key in new[] { "tools", "functions", "reasoning_format", "api_key", "max_tokens" }) Assert.False(root.TryGetProperty(key, out _));
        var messages = root.GetProperty("messages"); Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString()); Assert.Equal("user", messages[1].GetProperty("role").GetString());
        var system = messages[0].GetProperty("content").GetString()!;
        Assert.DoesNotContain(Canary, system); Assert.Equal(Canary, messages[1].GetProperty("content").GetString());
        Assert.Contains("DEN", system); Assert.Contains("SCADA", system);
        foreach (var scope in new[] { "threshold", "history", "OR", "multiple", "Unsupported", "NeedsClarification", "literal", "pageSize" }) Assert.Contains(scope, system);
        var format = root.GetProperty("response_format"); Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var schema = format.GetProperty("json_schema"); Assert.True(schema.GetProperty("strict").GetBoolean());
        var proposal = schema.GetProperty("schema"); Required(proposal, ["outcome", "query", "reasonCode"]);
        var properties = proposal.GetProperty("properties");
        Assert.Equal(new[] { "Ready", "NeedsClarification", "Unsupported" }, Strings(properties.GetProperty("outcome").GetProperty("enum")));
        Assert.Equal(new[] { "ready", "ambiguous", "unsupported", "no_interpretation" }, Strings(properties.GetProperty("reasonCode").GetProperty("enum")));
        var query = properties.GetProperty("query").GetProperty("anyOf"); Assert.Equal("null", query[1].GetProperty("type").GetString());
        Required(query[0], ["q", "site", "type", "status", "sort", "direction"]);
        var filters = query[0].GetProperty("properties");
        Assert.Equal(new[] { "string", "null" }, Strings(filters.GetProperty("q").GetProperty("type")));
        foreach (var (key, tokens) in new[] { ("site", new[] { "DEN" }), ("type", new[] { "Server", "SCADA" }),
            ("status", new[] { "Unknown", "Healthy", "Degraded", "Offline" }) })
        {
            var values = filters.GetProperty(key).GetProperty("enum");
            Assert.Equal(JsonValueKind.Null, values[values.GetArrayLength() - 1].ValueKind);
            Assert.Equal(tokens, values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()));
        }
        Assert.Equal(new[] { "displayName", "assetTag", "siteCode", "status", "lastObservedAtUtc", "latencyMs", "cpuPercent" }, Strings(filters.GetProperty("sort").GetProperty("enum")));
        Assert.Equal(new[] { "asc", "desc" }, Strings(filters.GetProperty("direction").GetProperty("enum")));
    }

    [Fact]
    public void EmptyVocabulary_ProducesNullOnlyFilterSchemas()
    {
        using var document = JsonDocument.Parse(GroqQueryProtocol.CreateRequestBody("all devices", new([], [])));
        var filters = document.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema")
            .GetProperty("properties").GetProperty("query").GetProperty("anyOf")[0].GetProperty("properties");
        foreach (var key in new[] { "site", "type" }) Assert.Equal("null", filters.GetProperty(key).GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData(" x")] [InlineData("x ")]
    public void InvalidPrompt_RejectsWithoutEcho(string? prompt) =>
        Assert.Equal("The prompt must contain 1-500 characters after trimming.",
            Assert.Throws<InvalidAiPromptException>(() => GroqQueryProtocol.CreateRequestBody(prompt, Vocabulary)).Message);

    [Fact]
    public void PromptBoundary_IsUtf16AndVocabularyIsRequired()
    {
        Assert.NotEmpty(GroqQueryProtocol.CreateRequestBody("x", Vocabulary));
        Assert.NotEmpty(GroqQueryProtocol.CreateRequestBody(new string('x', 500), Vocabulary));
        Assert.NotEmpty(GroqQueryProtocol.CreateRequestBody(string.Concat(Enumerable.Repeat("😀", 250)), Vocabulary));
        Assert.Throws<InvalidAiPromptException>(() => GroqQueryProtocol.CreateRequestBody(new string('x', 501), Vocabulary));
        Assert.Throws<InvalidAiPromptException>(() => GroqQueryProtocol.CreateRequestBody(string.Concat(Enumerable.Repeat("😀", 251)), Vocabulary));
        Assert.Throws<ArgumentNullException>(() => GroqQueryProtocol.CreateRequestBody("x", null!));
    }

    [Theory]
    [InlineData("Ready", "ready")] [InlineData("NeedsClarification", "ambiguous")]
    [InlineData("NeedsClarification", "no_interpretation")] [InlineData("Unsupported", "unsupported")]
    public async Task ValidEnvelope_ComposesWithRealConsumer(string outcome, string reason)
    {
        var node = JsonNode.Parse(Proposal)!; node["outcome"] = outcome; node["reasonCode"] = reason;
        if (outcome != "Ready") node["query"] = null;
        var envelope = Envelope(node.ToJsonString());
        var result = await new TranslateDeviceQuery(new ParsingGenerator(envelope.ToJsonString()), Vocabulary).TranslateAsync("devices", default);
        Assert.Equal(outcome, result.Outcome); Assert.Equal(outcome == "Ready", result.Query is not null);
    }

    [Fact]
    public void MeaningfulFields_AllMissingAndWrongTypesReject()
    {
        foreach (var level in new[] { "root", "choice", "message" })
        foreach (var field in level == "root" ? new[] { "model", "choices" } : level == "choice" ? new[] { "index", "finish_reason", "message" } : new[] { "role", "content" })
        {
            var node = Envelope(); Target(node, level).Remove(field); Invalid(node.ToJsonString());
            foreach (var value in new[] { "null", "true", "[]", "{}", "\"wrong\"", "1" })
            { node = Envelope(); Target(node, level)[field] = JsonNode.Parse(value); Invalid(node.ToJsonString()); }
        }
        foreach (var finish in new[] { "length", "tool_calls", "content_filter", "Stop", " stop" })
        { var node = Envelope(); Target(node, "choice")["finish_reason"] = finish; Invalid(node.ToJsonString()); }
        var empty = Envelope(); empty["choices"] = new JsonArray(); Invalid(empty.ToJsonString());
        var multiple = Envelope(); multiple["choices"]!.AsArray().Add(multiple["choices"]![0]!.DeepClone()); Invalid(multiple.ToJsonString());
    }

    [Fact]
    public void RefusalReasoningAndTools_AreNotAcceptedAsProposals()
    {
        foreach (var key in new[] { "refusal", "reasoning" })
        {
            foreach (var value in new[] { "null", "\"\"" })
            { var node = Envelope(); Target(node, "message")[key] = JsonNode.Parse(value); Assert.NotNull(Parse(node.ToJsonString())); }
            foreach (var value in new[] { "\"" + Canary + "\"", "0", "{}", "[]" })
            { var node = Envelope(); Target(node, "message")[key] = JsonNode.Parse(value); Invalid(node.ToJsonString()); }
        }
        foreach (var key in new[] { "error", "function_call" })
        foreach (var value in new[] { "\"\"", "{}", "0", "[]" })
        { var node = Envelope(); (key == "error" ? node : Target(node, "message"))[key] = JsonNode.Parse(value); Invalid(node.ToJsonString()); }
        foreach (var value in new[] { "null", "[]" })
        { var node = Envelope(); Target(node, "message")["tool_calls"] = JsonNode.Parse(value); Assert.NotNull(Parse(node.ToJsonString())); }
        foreach (var value in new[] { "\"\"", "{}", "0", "[{}]" })
        { var node = Envelope(); Target(node, "message")["tool_calls"] = JsonNode.Parse(value); Invalid(node.ToJsonString()); }
    }

    [Fact]
    public void Metadata_IsForwardCompatibleButStillStrictlyDecoded()
    {
        var node = Envelope(); node["unknown"] = JsonNode.Parse("""{"nested":["text",null,{"allowed":true}]}""");
        node["usage"] = JsonNode.Parse("""{"completion_tokens_details":{"reasoning_tokens":27}}""");
        Assert.NotNull(Parse(node.ToJsonString()));
        foreach (var value in new[] { "\"\\uD800\"", "{\"\\uDC00\":1}", "{\"x\":1,\"x\":2}" })
            Invalid(node.ToJsonString()[..^1] + ",\"malformed\":" + value + "}");
    }

    [Fact]
    public void DuplicateDecodedKeys_RejectAtEveryLevel()
    {
        var json = Envelope().ToJsonString();
        foreach (var key in new[] { "model", "choices", "index", "finish_reason", "message", "role", "content" })
        {
            var marker = "\"" + key + "\":";
            Invalid(json.Replace(marker, marker + "null," + marker, StringComparison.Ordinal));
            var alias = "\"\\u" + ((int)key[0]).ToString("x4") + key[1..] + "\":";
            Invalid(json.Replace(marker, alias + "null," + marker, StringComparison.Ordinal));
        }
        Assert.NotNull(Parse(json.Replace("\"model\"", "\"mod\\u0065l\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void ByteEncodingDepthAndWholeEnvelopeBounds_AreEnforced()
    {
        var json = Envelope().ToJsonString(); var bytes = Encoding.UTF8.GetBytes(json);
        Assert.NotNull(Parse(json + new string(' ', 8192 - bytes.Length)));
        Invalid(json + new string(' ', 8193 - bytes.Length));
        Invalid(json[..^1] + ",\"extra\":\"" + new string('界', 2730) + "\"}");
        Invalid(json[..^1] + ",\"extra\":" + new string('[', 20) + "0" + new string(']', 20) + "}");
        Invalid(Encoding.UTF8.GetPreamble().Concat(bytes).ToArray());
        Invalid(Encoding.UTF8.GetBytes(json[..^1] + ",\"extra\":\"").Concat(new byte[] { 0xff }).Concat(Encoding.UTF8.GetBytes("\"}")).ToArray());
        foreach (var text in new[] { "", "null", "[]", json[..^1], json + " null", "/*comment*/" + json, json[..^1] + ",}" }) Invalid(text);
    }

    [Fact]
    public void ProposalShapeAndSemantics_KeepTheirOwnBoundary()
    {
        Invalid(Envelope("```json\n" + Proposal + "\n```").ToJsonString());
        Invalid(Envelope(Proposal.Replace("\"q\":null,", "", StringComparison.Ordinal)).ToJsonString());
        var proposal = Parse(Envelope(Proposal.Replace("\"asc\"", "\"ASC\"", StringComparison.Ordinal)).ToJsonString());
        Assert.Null(StructuredQueryValidator.Validate(proposal, Vocabulary));
    }

    /// <summary>Builds controlled success metadata; no actual provider request is constructed or sent.</summary>
    private static JsonObject Envelope(string content = Proposal) => JsonSerializer.SerializeToNode(new
    {
        model = GroqQueryProtocol.Model,
        choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } }
    })!.AsObject();
    /// <summary>Selects the meaningful protocol level while preserving other valid envelope fields.</summary>
    private static JsonObject Target(JsonObject root, string level) => level switch
    { "root" => root, "choice" => root["choices"]![0]!.AsObject(), _ => root["choices"]![0]!["message"]!.AsObject() };
    /// <summary>Checks strict required fields without depending on schema property order.</summary>
    private static void Required(JsonElement schema, string[] keys)
    { Assert.False(schema.GetProperty("additionalProperties").GetBoolean()); Assert.Equal(keys.Order(), Strings(schema.GetProperty("required")).Order()); }
    /// <summary>Reads only test-owned string arrays from generated request JSON.</summary>
    private static string?[] Strings(JsonElement element) => element.EnumerateArray().Select(value => value.GetString()).ToArray();
    /// <summary>Encodes actual bytes so the production parser owns all decoding.</summary>
    private static StructuredQueryProposal Parse(string json) => GroqQueryProtocol.ParseEnvelope(Encoding.UTF8.GetBytes(json));
    /// <summary>Checks that expected parsing failures never expose data or raw diagnostics.</summary>
    private static void Invalid(string json) => Invalid(Encoding.UTF8.GetBytes(json));
    /// <summary>Exercises malformed byte sequences that cannot round-trip through a string.</summary>
    private static void Invalid(byte[] bytes)
    {
        var error = Assert.Throws<InvalidStructuredQueryException>(() => GroqQueryProtocol.ParseEnvelope(bytes));
        Assert.Equal("The generated query proposal is invalid.", error.Message); Assert.Null(error.InnerException);
    }
    /// <summary>Composes the protocol with the real Application consumer without transport or storage.</summary>
    private sealed class ParsingGenerator(string json) : IStructuredQueryGenerator
    {
        /// <inheritdoc />
        public Task<StructuredQueryProposal?> GenerateAsync(string prompt, QueryVocabulary vocabulary, CancellationToken cancellationToken) =>
            Task.FromResult<StructuredQueryProposal?>(Parse(json));
    }
}
