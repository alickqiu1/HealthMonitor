using HealthMonitor.Application.AiQueries;
using HealthMonitor.Api.Provisioning;
using Candidate = HealthMonitor.Application.AiQueries.DeviceGridQuery;
using ReadQuery = HealthMonitor.Api.Fleet.DeviceGridQuery;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Exercises semantic validation only; typed inputs do not prove strict provider JSON parsing.</summary>
public sealed class StructuredQueryValidatorTests
{
    private static readonly QueryVocabulary Vocabulary = new(
        FixedDeviceCatalog.Devices.Select(d => d.SiteCode), FixedDeviceCatalog.Devices.Select(d => d.DeviceType));
    private static readonly Candidate Neutral = new(null, null, null, null, "displayName", "asc");

    [Fact]
    public void OutcomeReasonAndQueryPresence_AllCombinationsAreChecked()
    {
        foreach (var outcome in new string?[] { null, "Ready", "NeedsClarification", "Unsupported", "ready", " Ready", "0", "<bad>" })
        foreach (var reason in new string?[] { null, "ready", "ambiguous", "no_interpretation", "unsupported", "Ready", "ready ", "0", "<bad>" })
        foreach (var query in new Candidate?[] { null, Neutral })
        {
            var valid = outcome == "Ready" && reason == "ready" && query is not null
                || outcome == "NeedsClarification" && reason is "ambiguous" or "no_interpretation" && query is null
                || outcome == "Unsupported" && reason == "unsupported" && query is null;
            var result = StructuredQueryValidator.Validate(new(outcome, query, reason), Vocabulary);
            Assert.Equal(valid, result is not null);
            if (result is not null)
            {
                Assert.Equal(outcome, result.Outcome);
                Assert.Equal(query is null, result.Query is null);
                Assert.Equal(outcome switch
                {
                    "Ready" => "Review the proposed filters before applying them.",
                    "NeedsClarification" => "Please name Healthy, Degraded, Offline, or Unknown and any desired site or device type.",
                    _ => "Only supported device filter queries can be proposed."
                }, result.Message);
            }
        }
        Assert.Null(StructuredQueryValidator.Validate(null, Vocabulary));
        Assert.Throws<ArgumentNullException>(() => StructuredQueryValidator.Validate(null, null!));
    }

    [Fact]
    public void EveryAllowedTokenCombination_RoundTripsThroughIndependentReadValidation()
    {
        foreach (var status in new string?[] { null, "Unknown", "Healthy", "Degraded", "Offline" })
        foreach (var sort in new[] { "displayName", "assetTag", "siteCode", "status", "lastObservedAtUtc", "latencyMs", "cpuPercent" })
        foreach (var direction in new[] { "asc", "desc" })
        foreach (var site in Vocabulary.Sites)
        foreach (var type in Vocabulary.Types)
        {
            var result = Validate(new("  select %_['  ", site.ToLowerInvariant(), type.ToUpperInvariant(), status, sort, direction));
            var query = Assert.IsType<Candidate>(Assert.IsType<QueryTranslationResult>(result).Query);
            Assert.Equal("select %_['", query.Q); Assert.Equal(site, query.Site); Assert.Equal(type, query.Type);
            var read = new ReadQuery(q: query.Q, site: query.Site, type: query.Type, status: query.Status,
                sort: query.Sort, direction: query.Direction);
            Assert.Equal(query.Q, read.Q); Assert.Equal(query.Site, read.Site); Assert.Equal(query.Type, read.Type);
            Assert.Equal(status, read.Status?.ToString()); Assert.Equal(direction == "desc", read.Descending);
            Assert.Equal(sort, char.ToLowerInvariant(read.Sort.ToString()[0]) + read.Sort.ToString()[1..]);
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("x", true)]
    [InlineData(" \tx\t ", true)]
    [InlineData("", false)]
    [InlineData(" \t ", false)]
    [InlineData("DROP TABLE devices;", true)]
    public void Search_IsNullOrBoundedTrimmedLiteral(string? q, bool valid) =>
        Assert.Equal(valid, Validate(Neutral with { Q = q }) is not null);

    [Fact]
    public void Search_LengthBoundaryIsAppliedAfterTrimming()
    {
        Assert.Equal(new string('x', 100), Validate(Neutral with { Q = " " + new string('x', 100) + " " })!.Query!.Q);
        Assert.Null(Validate(Neutral with { Q = new string('x', 101) }));
        // UTF-16 length matches the existing GET contract, including surrogate pairs.
        Assert.NotNull(Validate(Neutral with { Q = string.Concat(Enumerable.Repeat("😀", 50)) }));
        Assert.Null(Validate(Neutral with { Q = string.Concat(Enumerable.Repeat("😀", 51)) }));
    }

    [Fact]
    public void InvalidTokens_AreRejectedWithoutDefaultsOrApproximation()
    {
        foreach (var token in new string?[] { null, "", " ", "0", "unknown", "Healthy ", "ASC", "displayname", "<script>" })
        {
            Assert.Null(Validate(Neutral with { Sort = token }));
            Assert.Null(Validate(Neutral with { Direction = token }));
            if (token is not null)
            {
                Assert.Null(Validate(Neutral with { Status = token }));
                Assert.Null(Validate(Neutral with { Site = token }));
                Assert.Null(Validate(Neutral with { Type = token }));
            }
        }
        Assert.Equal("EDM", Validate(Neutral with { Site = " edm " })!.Query!.Site);
        Assert.Equal("SCADA", Validate(Neutral with { Type = " scada " })!.Query!.Type);
        Assert.NotNull(Validate(Neutral));
    }

    [Fact]
    public void ValidatedResult_CannotBeConstructedOrMutatedByExternalCallers()
    {
        Assert.Empty(typeof(QueryTranslationResult).GetConstructors());
        Assert.All(typeof(QueryTranslationResult).GetProperties(), property => Assert.Null(property.SetMethod));
        var result = Validate(Neutral)!;
        var changedCandidate = result.Query! with { Sort = "invalid" };
        Assert.Null(Validate(changedCandidate));
        Assert.Equal("displayName", result.Query!.Sort);
    }

    /// <summary>Routes a candidate through the same Ready decision without involving a provider or storage.</summary>
    private static QueryTranslationResult? Validate(Candidate query) =>
        StructuredQueryValidator.Validate(new("Ready", query, "ready"), Vocabulary);
}
