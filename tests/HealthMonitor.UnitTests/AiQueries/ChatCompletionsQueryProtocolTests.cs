using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HealthMonitor.Application.AiQueries;
using HealthMonitor.Infrastructure.AiQueries;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Verifies both documented wire contracts and strict selected-model parsing without credentials or HTTP.</summary>
public sealed class ChatCompletionsQueryProtocolTests
{
    private static readonly QueryVocabulary Vocabulary = new(["DEN"], ["Server"]);
    internal const string Ready = """
        {"outcome":"Ready","query":{"q":null,"site":"DEN","type":"Server","status":"Offline","sort":"displayName","direction":"asc"},"reasonCode":"ready"}
        """;

    /// <summary>Builds a synthetic provider envelope with optional documented usage metadata, never a live response.</summary>
    internal static byte[] Envelope(StructuredQueryProviderProfile profile, string? proposal = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = profile.Model,
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = proposal ?? Ready } } },
            usage = new { prompt_tokens = 10, completion_tokens = 20, total_tokens = 30 }
        });

    [Theory]
    [InlineData("Groq", "low")] [InlineData("OpenAI", "none")]
    public void Request_UsesProfileSpecificParametersAndCommonStrictSchema(string name, string reasoning)
    {
        const string prompt = "private-user-prompt-canary";
        using var json = JsonDocument.Parse(ChatCompletionsQueryProtocol.CreateRequestBody(StructuredQueryProviderProfile.FromName(name), prompt, Vocabulary));
        var body = json.RootElement;
        Assert.Equal(name == "Groq" ? "openai/gpt-oss-20b" : "gpt-5.4-mini-2026-03-17", body.GetProperty("model").GetString());
        Assert.Equal(reasoning, body.GetProperty("reasoning_effort").GetString());
        Assert.Equal(400, body.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal(1, body.GetProperty("n").GetInt32()); Assert.False(body.GetProperty("stream").GetBoolean());
        if (name == "Groq") { Assert.False(body.GetProperty("include_reasoning").GetBoolean()); Assert.False(body.TryGetProperty("store", out _)); }
        else { Assert.False(body.GetProperty("store").GetBoolean()); Assert.False(body.TryGetProperty("include_reasoning", out _)); }
        foreach (string excluded in new[] { "tools", "functions", "reasoning_format", "api_key", "max_tokens" })
            Assert.False(body.TryGetProperty(excluded, out _));
        Assert.Equal("system", body.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.DoesNotContain(prompt, body.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(prompt, body.GetProperty("messages")[1].GetProperty("content").GetString());
        var format = body.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.False(format.GetProperty("json_schema").GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
        var groq = JsonNode.Parse(ChatCompletionsQueryProtocol.CreateRequestBody(StructuredQueryProviderProfile.FromName("Groq"), "devices", Vocabulary));
        var openai = JsonNode.Parse(ChatCompletionsQueryProtocol.CreateRequestBody(StructuredQueryProviderProfile.FromName("OpenAI"), "devices", Vocabulary));
        Assert.True(JsonNode.DeepEquals(groq!["response_format"], openai!["response_format"]));
    }

    [Theory]
    [InlineData("Groq", "Ready", "ready")] [InlineData("OpenAI", "Ready", "ready")]
    [InlineData("Groq", "NeedsClarification", "ambiguous")] [InlineData("OpenAI", "NeedsClarification", "ambiguous")]
    [InlineData("Groq", "Unsupported", "unsupported")] [InlineData("OpenAI", "Unsupported", "unsupported")]
    public void DocumentedEnvelope_AllOutcomesAndUsageMetadataRemainUntrustedCandidates(string name, string outcome, string reason)
    {
        var profile = StructuredQueryProviderProfile.FromName(name);
        string proposal = outcome == "Ready" ? Ready : JsonSerializer.Serialize(new { outcome, query = (object?)null, reasonCode = reason });
        var result = ChatCompletionsQueryProtocol.ParseEnvelope(profile, Envelope(profile, proposal));
        Assert.Equal(outcome, result.Outcome); Assert.Equal(reason, result.ReasonCode);
        Assert.Equal(outcome == "Ready", result.Query is not null);
    }

    [Theory]
    [InlineData("Groq")] [InlineData("OpenAI")]
    public void Envelope_MustReturnTheSelectedProfileModel(string name)
    {
        var selected = StructuredQueryProviderProfile.FromName(name);
        var other = StructuredQueryProviderProfile.FromName(name == "Groq" ? "OpenAI" : "Groq");
        Assert.Throws<InvalidStructuredQueryException>(() => ChatCompletionsQueryProtocol.ParseEnvelope(selected, Envelope(other)));
    }

    [Theory]
    [InlineData("Groq")] [InlineData("OpenAI")]
    public void Output_RejectsRefusalTruncationToolsAndReturnedReasoningWithoutEcho(string name)
    {
        var profile = StructuredQueryProviderProfile.FromName(name);
        foreach (string violation in new[] { "refusal", "reasoning", "length", "tool_calls", "proposal", "model" })
        {
            var root = JsonNode.Parse(Envelope(profile))!.AsObject();
            var choice = root["choices"]![0]!;
            var message = choice["message"]!;
            switch (violation)
            {
                case "length": choice["finish_reason"] = "length"; break;
                case "tool_calls": message["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "private-output-canary" }); break;
                case "proposal": message["content"] = "private-output-canary"; break;
                case "model": root["model"] = "private-output-canary"; break;
                default: message[violation] = "private-output-canary"; break;
            }
            var error = Assert.Throws<InvalidStructuredQueryException>(() =>
                ChatCompletionsQueryProtocol.ParseEnvelope(profile, JsonSerializer.SerializeToUtf8Bytes(root)));
            Assert.DoesNotContain("private-output-canary", error.ToString());
        }
    }

    [Theory]
    [InlineData("Groq")] [InlineData("OpenAI")]
    public void Envelope_EnforcesExactByteBoundaryDuplicateAndUtf8Rules(string name)
    {
        var profile = StructuredQueryProviderProfile.FromName(name);
        var valid = Encoding.UTF8.GetString(Envelope(profile));
        var boundary = Encoding.UTF8.GetBytes(valid + new string(' ', 8192 - Encoding.UTF8.GetByteCount(valid)));
        Assert.Equal("Ready", ChatCompletionsQueryProtocol.ParseEnvelope(profile, boundary).Outcome);
        Assert.Throws<InvalidStructuredQueryException>(() => ChatCompletionsQueryProtocol.ParseEnvelope(profile, boundary.Concat(new byte[] { 32 }).ToArray()));
        Assert.Throws<InvalidStructuredQueryException>(() => ChatCompletionsQueryProtocol.ParseEnvelope(profile, new byte[] { 0xff }));
        var duplicate = Encoding.UTF8.GetBytes("{\"model\":\"private-output-canary\"," + valid[1..]);
        Assert.Throws<InvalidStructuredQueryException>(() => ChatCompletionsQueryProtocol.ParseEnvelope(profile, duplicate));
    }

    [Fact]
    public void MissingProfile_IsAContractErrorWithoutParsingOrCreatingTransport()
    {
        Assert.Throws<ArgumentNullException>(() => ChatCompletionsQueryProtocol.CreateRequestBody(null!, "devices", Vocabulary));
        Assert.Throws<ArgumentNullException>(() => ChatCompletionsQueryProtocol.ParseEnvelope(null!, Array.Empty<byte>()));
    }
}
