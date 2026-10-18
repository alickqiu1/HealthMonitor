using System.Text;
using System.Text.Json.Nodes;
using HealthMonitor.Application.AiQueries;
using HealthMonitor.Application.AiQueries.Ports;
using HealthMonitor.Infrastructure.AiQueries;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Exercises actual UTF-8 and JSON structure independently of provider transport or storage.</summary>
public sealed class StructuredQueryProposalParserTests
{
    private const string ReadyJson = """
        {"outcome":"Ready","query":{"q":null,"site":null,"type":null,"status":null,"sort":"displayName","direction":"asc"},"reasonCode":"ready"}
        """;

    [Fact]
    public void CompleteReady_ProducesDetachedCandidatesAndDecodesEscapes()
    {
        var node = JsonNode.Parse(ReadyJson)!;
        node["query"]!["q"] = "literal %_[' 😀";
        node["query"]!["site"] = " den ";
        node["query"]!["type"] = "server";
        node["query"]!["status"] = "Offline";
        var result = Parse(node.ToJsonString());
        Assert.Equal("Ready", result.Outcome); Assert.Equal("ready", result.ReasonCode);
        Assert.Equal(new DeviceGridQuery("literal %_[' 😀", " den ", "server", "Offline", "displayName", "asc"), result.Query);
        var escaped = ReadyJson.Replace("\"outcome\"", "\"outc\\u006fme\"", StringComparison.Ordinal);
        Assert.Equal("Ready", Parse(escaped).Outcome);
    }

    [Theory]
    [InlineData("NeedsClarification", "ambiguous")]
    [InlineData("NeedsClarification", "no_interpretation")]
    [InlineData("Unsupported", "unsupported")]
    public void NullQuery_IsStructurallyValid(string outcome, string reason)
    {
        var result = Parse($$"""{"outcome":"{{outcome}}","query":null,"reasonCode":"{{reason}}"}""");
        Assert.Equal(outcome, result.Outcome); Assert.Null(result.Query); Assert.Equal(reason, result.ReasonCode);
    }

    [Fact]
    public void EveryMissingOrMisCasedField_IsRejectedAtBothLevels()
    {
        foreach (var queryLevel in new[] { false, true })
        foreach (var field in queryLevel ? new[] { "q", "site", "type", "status", "sort", "direction" }
            : new[] { "outcome", "query", "reasonCode" })
        {
            var node = JsonNode.Parse(ReadyJson)!.AsObject();
            var target = queryLevel ? node["query"]!.AsObject() : node;
            target.Remove(field);
            AssertInvalid(node.ToJsonString());
            target[char.ToUpperInvariant(field[0]) + field[1..]] = "value";
            AssertInvalid(node.ToJsonString());
        }
    }

    [Fact]
    public void EveryStringField_RejectsWrongJsonTypes_AndOnlyFourFiltersAllowNull()
    {
        foreach (var queryLevel in new[] { false, true })
        foreach (var field in queryLevel ? new[] { "q", "site", "type", "status", "sort", "direction" }
            : new[] { "outcome", "reasonCode" })
        foreach (var value in new[] { "0", "true", "[]", "{}", "null" })
        {
            var node = JsonNode.Parse(ReadyJson)!.AsObject();
            var target = queryLevel ? node["query"]!.AsObject() : node;
            target[field] = JsonNode.Parse(value);
            if (value == "null" && queryLevel && field is "q" or "site" or "type" or "status")
                Assert.NotNull(Parse(node.ToJsonString()));
            else AssertInvalid(node.ToJsonString());
        }
        foreach (var wrongQuery in new[] { "[]", "0", "true", "\"query\"" })
        {
            var node = JsonNode.Parse(ReadyJson)!;
            node["query"] = JsonNode.Parse(wrongQuery);
            AssertInvalid(node.ToJsonString());
        }
    }

    [Theory]
    [InlineData("outcome")]
    [InlineData("query")]
    [InlineData("reasonCode")]
    [InlineData("q")]
    [InlineData("site")]
    [InlineData("type")]
    [InlineData("status")]
    [InlineData("sort")]
    [InlineData("direction")]
    public void DuplicateFields_IncludingDecodedAliases_AreRejected(string field)
    {
        var marker = "\"" + field + "\":";
        AssertInvalid(ReadyJson.Replace(marker, marker + "null," + marker, StringComparison.Ordinal));
        var escaped = "\"\\u" + ((int)field[0]).ToString("x4") + field[1..] + "\":";
        AssertInvalid(ReadyJson.Replace(marker, escaped + "null," + marker, StringComparison.Ordinal));
    }

    [Fact]
    public void AdditionalFields_AtEitherLevelAreRejected()
    {
        foreach (var queryLevel in new[] { false, true })
        {
            var node = JsonNode.Parse(ReadyJson)!.AsObject();
            (queryLevel ? node["query"]!.AsObject() : node)["sql"] = "SELECT private-value";
            AssertInvalid(node.ToJsonString());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("0")]
    [InlineData("{}")]
    [InlineData("{unquoted:true}")]
    public void InvalidDocument_IsRejected(string json) => AssertInvalid(json);

    [Fact]
    public void CommentsTrailingCommasCodeFencesAndMultipleValues_AreNotRepaired()
    {
        AssertInvalid("/* comment */" + ReadyJson);
        AssertInvalid(ReadyJson[..^1] + ",}");
        AssertInvalid("```json\n" + ReadyJson + "\n```");
        AssertInvalid(ReadyJson + " null");
        AssertInvalid(ReadyJson + ReadyJson);
        AssertInvalid(ReadyJson[..^1]);
        AssertInvalid(ReadyJson.Replace("\"q\":null", "\"q\":{\"x\":[[[[[0]]]]]}", StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedUtf8AndUnpairedEscapedSurrogates_AreRejectedWithoutReplacement()
    {
        var parts = ReadyJson.Replace("\"q\":null", "\"q\":\"SENTINEL\"", StringComparison.Ordinal)
            .Split("SENTINEL", StringSplitOptions.None);
        var prefix = Encoding.UTF8.GetBytes(parts[0]);
        var suffix = Encoding.UTF8.GetBytes(parts[1]);
        foreach (var malformed in new byte[][] { [0xc3, 0x28], [0xed, 0xa0, 0x80], [0xff], [0xe2, 0x82] })
            AssertInvalid(prefix.Concat(malformed).Concat(suffix).ToArray());
        foreach (var escape in new[] { "\\uD800", "\\uDC00", "\\uD800x" })
        {
            AssertInvalid(ReadyJson.Replace("\"q\":null", "\"q\":\"" + escape + "\"", StringComparison.Ordinal));
            AssertInvalid(ReadyJson.Replace("\"outcome\"", "\"" + escape + "\"", StringComparison.Ordinal));
        }
        AssertInvalid(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ReadyJson)).ToArray());
    }

    [Fact]
    public void ByteLimit_IsInclusiveAndUsesActualUtf8Size()
    {
        var length = Encoding.UTF8.GetByteCount(ReadyJson);
        Assert.NotNull(Parse(ReadyJson + new string(' ', 8192 - length)));
        AssertInvalid(ReadyJson + new string(' ', 8193 - length));
        var unicode = ReadyJson.Replace("\"q\":null", "\"q\":\"" + new string('€', 2700) + "\"", StringComparison.Ordinal);
        Assert.True(unicode.Length < 8192);
        Assert.True(Encoding.UTF8.GetByteCount(unicode) > 8192);
        AssertInvalid(unicode);
    }

    [Fact]
    public async Task ConsumerComposition_SeparatesStrictStructureFromSemanticValidation()
    {
        var vocabulary = new QueryVocabulary(["DEN"], ["Server"]);
        Assert.Equal("Ready", (await new TranslateDeviceQuery(new ParsingGenerator(ReadyJson), vocabulary)
            .TranslateAsync("devices", default)).Outcome);
        foreach (var json in new[] { ReadyJson.Replace("\"asc\"", "\"ASC\"", StringComparison.Ordinal),
            ReadyJson.Replace("\"q\":null,", "", StringComparison.Ordinal) })
        {
            var error = await Assert.ThrowsAsync<InvalidStructuredQueryException>(() =>
                new TranslateDeviceQuery(new ParsingGenerator(json), vocabulary).TranslateAsync("private prompt", default));
            Assert.Equal("The generated query proposal is invalid.", error.Message);
            Assert.Null(error.InnerException);
        }
        // Structural validity must not silently imply that the outcome/query combination is semantically valid.
        Assert.Null(Parse("""{"outcome":"Ready","query":null,"reasonCode":"ready"}""").Query);
        Assert.Null(StructuredQueryValidator.Validate(Parse("""{"outcome":"Ready","query":null,"reasonCode":"ready"}"""), vocabulary));
    }

    /// <summary>Encodes test text as UTF-8 so assertions exercise the production byte-oriented entry point.</summary>
    private static StructuredQueryProposal Parse(string json) => StructuredQueryProposalParser.Parse(Encoding.UTF8.GetBytes(json));

    /// <summary>Checks the fixed failure contract without retaining parser diagnostics as an inner exception.</summary>
    private static void AssertInvalid(string json) => AssertInvalid(Encoding.UTF8.GetBytes(json));

    /// <summary>Exercises malformed bytes that cannot safely round-trip through a .NET string.</summary>
    private static void AssertInvalid(byte[] bytes)
    {
        var error = Assert.Throws<InvalidStructuredQueryException>(() => StructuredQueryProposalParser.Parse(bytes));
        Assert.Equal("The generated query proposal is invalid.", error.Message);
        Assert.Null(error.InnerException);
    }

    /// <summary>Connects strict parsing to the real consumer without an HTTP client, provider SDK, or storage.</summary>
    private sealed class ParsingGenerator(string json) : IStructuredQueryGenerator
    {
        /// <inheritdoc />
        public Task<StructuredQueryProposal?> GenerateAsync(string prompt, QueryVocabulary vocabulary,
            CancellationToken cancellationToken) => Task.FromResult<StructuredQueryProposal?>(Parse(json));
    }
}
