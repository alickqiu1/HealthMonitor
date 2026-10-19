using HealthMonitor.Api.AiQueries;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.FileProviders;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Exercises active settings only with fictional in-memory keys and lexical paths; no storage or provider is accessed.</summary>
public sealed class AiQueryOptionsTests
{
    private const string Canary = "fictional-private-canary";
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "HealthMonitor-options-fictional");
    private static string Counter => Path.Combine(Root, "private", "usage.json");

    /// <summary>Supplies deterministic nonexistent path strings; creating an environment never creates a directory.</summary>
    private static TestEnvironment Environment() => new()
    { ContentRootPath = Path.Combine(Root, "host"), WebRootPath = Path.Combine(Root, "host", "wwwroot") };

    /// <summary>Builds synthetic active settings; overrides can represent explicit nulls or invalid leaf shapes.</summary>
    private static ConfigurationRoot Configuration(params (string Key, string? Value)[] changes)
    {
        var values = new Dictionary<string, string?>
        { ["AiQueries:Enabled"] = "true", ["AiQueries:ApiKey"] = Canary, ["AiQueries:BudgetCounterPath"] = Counter };
        foreach (var (key, value) in changes) values["AiQueries:" + key] = value;
        return new ConfigurationRoot([new MemoryConfigurationProvider(new MemoryConfigurationSource { InitialData = values })]);
    }

    /// <summary>Checks complete exceptions, including nested diagnostics, for fictional credential/path leakage.</summary>
    private static void Rejected(IConfiguration configuration, string? key = null, IWebHostEnvironment? environment = null)
    {
        var error = Assert.Throws<InvalidOperationException>(() => AiQueryOptions.FromConfiguration(configuration, environment ?? Environment()));
        Assert.DoesNotContain(Canary, error.ToString());
        Assert.DoesNotContain(Root, error.ToString());
        Assert.Contains("AiQueries", error.Message);
        if (key is not null) Assert.Contains("AiQueries:" + key, error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(null, "Groq")] [InlineData("Groq", "Groq")] [InlineData("openai", "OpenAI")]
    public void Active_ResolvesSelectedProfileAndAbsoluteCounterSnapshot(string? selected, string expected)
    {
        using var configuration = Configuration();
        if (selected is not null) configuration["AiQueries:Provider"] = selected;
        var options = AiQueryOptions.FromConfiguration(configuration, Environment());
        Assert.True(options.Enabled); Assert.Equal(expected, options.Profile!.Name);
        Assert.Equal(Canary, options.ApiKey); Assert.Equal(Path.GetFullPath(Counter), options.BudgetCounterPath);
        Assert.DoesNotContain(Canary, options.ToString());
        configuration["AiQueries:Enabled"] = "false"; configuration["AiQueries:ApiKey"] = "changed";
        configuration["AiQueries:Provider"] = "unsupported";
        Assert.True(options.Enabled); Assert.Equal(Canary, options.ApiKey); Assert.Equal(expected, options.Profile.Name);
    }

    [Theory]
    [InlineData(null)] [InlineData("false")] [InlineData("False")]
    public void Disabled_DoesNotReadSensitiveValuesOrHostPaths(string? flag)
    {
        var provider = new SensitiveValueGuardProvider(flag);
        using var configuration = new ConfigurationRoot([provider]);
        var options = AiQueryOptions.FromConfiguration(configuration, new TestEnvironment { ThrowOnPathAccess = true });
        Assert.False(options.Enabled); Assert.Null(options.Profile); Assert.Null(options.ApiKey); Assert.Null(options.BudgetCounterPath);
        Assert.Equal(0, provider.SensitiveReads);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" true ")] [InlineData("1")] [InlineData("fictional-private-canary")]
    public void InvalidEnabled_UsesSameStrictLeafRulesAsComposition(string? value) => Rejected(Configuration(("Enabled", value)), "Enabled");

    [Theory]
    [InlineData("Provider", null)] [InlineData("Provider", "")] [InlineData("Provider", "Groq ")]
    [InlineData("Provider", "fictional-private-canary")] [InlineData("ApiKey", null)] [InlineData("ApiKey", "")]
    [InlineData("ApiKey", "Bearer fictional-private-canary")] [InlineData("ApiKey", "a\r\nb")]
    [InlineData("ApiKey", "a:b")] [InlineData("ApiKey", "a b")] [InlineData("ApiKey", "a\u00e9")]
    [InlineData("BudgetCounterPath", null)] [InlineData("BudgetCounterPath", "")]
    [InlineData("BudgetCounterPath", "fictional-private-canary.json")]
    [InlineData("BudgetCounterPath", @"\\fictional-private-canary\share\usage.json")]
    public void InvalidActiveLeaf_FailsWithoutValues(string name, string? value) => Rejected(Configuration((name, value)), name);

    [Theory]
    [InlineData("Provider:child")] [InlineData("ApiKey:child")] [InlineData("BudgetCounterPath:child")]
    public void NonLeafActiveSetting_IsRejected(string name) => Rejected(Configuration((name, Canary)), name.Split(':')[0]);

    [Theory]
    [InlineData("Model", "openai/gpt-oss-20b")]
    [InlineData("BaseUrl", "https://api.groq.com/openai/v1/")]
    [InlineData("BaseUrl", "http://127.0.0.1/private")]
    [InlineData("BaseUrl", "https://api.groq.com:444/openai/v1/")]
    [InlineData("BaseUrl", "https://user:fictional-private-canary@api.groq.com/")]
    [InlineData("BaseUrl", "https://api.groq.com/openai/v1/?secret=fictional-private-canary#fragment")]
    [InlineData("ProviderEndpoint", "https://fictional-private-canary.invalid")]
    public void ObsoleteOrArbitraryTargetKnob_IsNotSilentlyIgnored(string name, string value) => Rejected(Configuration((name, value)));

    [Fact]
    public void MissingActualEnvironment_RejectsActiveBindingWithoutFileAccess() =>
        Assert.Throws<InvalidOperationException>(() => AiQueryOptions.FromConfiguration(Configuration()));

    [Fact]
    public void CounterPath_UsesConfiguredActualWebRootAndRejectsLexicalDescendants()
    {
        var environment = Environment();
        environment.WebRootPath = Path.Combine(Root, "custom-public");
        foreach (string path in new[] { environment.WebRootPath, Path.Combine(environment.WebRootPath, "usage.json"),
            Path.Combine(environment.WebRootPath, "nested", "..", "usage.json"), Counter + Path.DirectorySeparatorChar,
            Counter + "\0", Counter + "\nprivate" })
            Rejected(Configuration(("BudgetCounterPath", path)), "BudgetCounterPath", environment);
        var sibling = Path.Combine(Root, "custom-public-private", "usage.json");
        Assert.Equal(sibling, AiQueryOptions.FromConfiguration(Configuration(("BudgetCounterPath", sibling)), environment).BudgetCounterPath);
    }

    [Fact]
    public void MissingWebRoot_UsesHostContentRootRatherThanWorkingDirectory()
    {
        var environment = Environment(); environment.WebRootPath = null!;
        Rejected(Configuration(("BudgetCounterPath", Path.Combine(environment.ContentRootPath, "wwwroot", "usage.json"))),
            "BudgetCounterPath", environment);
        environment.ContentRootPath = "relative-host-root";
        Rejected(Configuration(), "BudgetCounterPath", environment);
    }

    [Theory]
    [InlineData("TimeoutSeconds", "10")] [InlineData("MaxPromptCharacters", "500")]
    [InlineData("MaxRequestBodyBytes", "8192")] [InlineData("MaxResponseBytes", "8192")]
    [InlineData("MaxOutputTokens", "400")] [InlineData("RequestsPerClientPerMinute", "5")]
    [InlineData("RequestsGlobalPerMinute", "10")] [InlineData("MaxConcurrentRequests", "2")]
    [InlineData("DailyRequestLimit", "100")]
    public void ArchitectureLimits_OnlyAcceptExactSupportedConstant(string name, string expected)
    {
        Assert.True(AiQueryOptions.FromConfiguration(Configuration((name, expected)), Environment()).Enabled);
        foreach (string? bad in new[] { null, "", "0", "-1", "99999", " " + expected, "0" + expected })
            Rejected(Configuration((name, bad)), name);
        Rejected(Configuration((name + ":child", Canary)), name);
    }

    /// <summary>Fails if disabled binding asks a provider for any AI value other than the enablement leaf.</summary>
    /// <remarks>Parent child-key metadata remains available to distinguish explicit-null enablement from absence.</remarks>
    private sealed class SensitiveValueGuardProvider : ConfigurationProvider
    {
        internal int SensitiveReads { get; private set; }
        internal SensitiveValueGuardProvider(string? flag)
        {
            if (flag is not null) Data["AiQueries:Enabled"] = flag;
            Data["AiQueries:ApiKey"] = Canary; Data["AiQueries:Provider"] = "invalid"; Data["AiQueries:BudgetCounterPath"] = "invalid";
        }
        public override bool TryGet(string key, out string? value)
        {
            if (key.StartsWith("AiQueries:", StringComparison.OrdinalIgnoreCase) && key != "AiQueries:Enabled")
            { SensitiveReads++; throw new InvalidOperationException("Sensitive value must not be read."); }
            return base.TryGet(key, out value);
        }
    }

    /// <summary>Models host-supplied path context only, without ownership proof or file access; the guard detects disabled path reads.</summary>
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        private string content = "";
        private string web = "";
        internal bool ThrowOnPathAccess { get; init; }
        public string ContentRootPath { get => ThrowOnPathAccess ? throw new InvalidOperationException("Host path must not be read.") : content; set => content = value; }
        public string WebRootPath { get => ThrowOnPathAccess ? throw new InvalidOperationException("Host path must not be read.") : web; set => web = value; }
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "FictionalOptionsHost";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}