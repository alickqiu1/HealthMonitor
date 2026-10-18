using HealthMonitor.Application.AiQueries;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Verifies owned canonical spelling, defensive snapshots, and deterministic configuration failures.</summary>
public sealed class QueryVocabularyTests
{
    [Fact]
    public void Snapshot_PreservesOwnedSpellingAndCannotBeChangedThroughCallerCollections()
    {
        var sites = new List<string> { "Site-one", "Site-one" };
        var types = new List<string> { "SCADA", "Server", "Server" };
        var vocabulary = new QueryVocabulary(sites, types);
        sites.Clear(); types[0] = "changed";
        Assert.Equal(new[] { "Site-one" }, vocabulary.Sites);
        Assert.Equal(new[] { "SCADA", "Server" }, vocabulary.Types);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)vocabulary.Sites)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)vocabulary.Types).Add("changed"));
        var result = StructuredQueryValidator.Validate(new("Ready",
            new(null, " site-ONE ", " scada ", null, "displayName", "asc"), "ready"), vocabulary);
        Assert.Equal("Site-one", result!.Query!.Site); Assert.Equal("SCADA", result.Query.Type);
    }

    [Fact]
    public void EmptyVocabulary_OnlyAllowsNullCorrespondingFilters()
    {
        var vocabulary = new QueryVocabulary([], []);
        var query = new DeviceGridQuery(null, null, null, null, "displayName", "asc");
        Assert.NotNull(StructuredQueryValidator.Validate(new("Ready", query, "ready"), vocabulary));
        Assert.Null(StructuredQueryValidator.Validate(new("Ready", query with { Site = "EDM" }, "ready"), vocabulary));
        Assert.Null(StructuredQueryValidator.Validate(new("Ready", query with { Type = "Server" }, "ready"), vocabulary));
    }

    [Fact]
    public void InvalidOwnedTokensAndCaseCollisions_RejectWithoutEchoingContents()
    {
        foreach (var values in new string[][] { [null!], [""], [" "], [" secret-token"], ["secret-token "],
            ["secret-token", "SECRET-TOKEN"], ["SECRET-TOKEN", "secret-token"] })
        {
            var sites = Assert.Throws<ArgumentException>(() => new QueryVocabulary(values, []));
            var types = Assert.Throws<ArgumentException>(() => new QueryVocabulary([], values));
            Assert.DoesNotContain("secret-token", sites.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-token", types.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Throws<ArgumentNullException>(() => new QueryVocabulary(null!, []));
        Assert.Throws<ArgumentNullException>(() => new QueryVocabulary([], null!));
    }
}
