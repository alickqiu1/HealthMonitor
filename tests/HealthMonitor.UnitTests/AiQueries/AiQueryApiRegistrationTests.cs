using HealthMonitor.Api.AiQueries;
using HealthMonitor.Api.Composition;
using HealthMonitor.Api.Controllers;
using HealthMonitor.Application.AiQueries;
using HealthMonitor.Application.AiQueries.Ports;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Verifies the temporary production gate without enabling a host, loading secrets, or registering a provider.</summary>
public sealed class AiQueryApiRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("False")]
    public void Disabled_RegistersOnlyLazyCompositionAndRemovesAiController(string? flag)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(flag is null ? []
            : new Dictionary<string, string?> { ["AiQueries:Enabled"] = flag }).Build();
        var services = new ServiceCollection();
        services.AddLogging(); services.AddReadApi(); services.AddReadCors(configuration); services.AddAiQueryApi(configuration);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IStructuredQueryGenerator));
        using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<ApplicationPartManager>();
        var feature = new ControllerFeature(); manager.PopulateFeature(feature);
        Assert.DoesNotContain(feature.Controllers, type => type.AsType() == typeof(AiDeviceQueriesController));
        Assert.Contains(feature.Controllers, type => type.AsType() == typeof(DevicesController));
        var vocabulary = provider.GetRequiredService<QueryVocabulary>();
        Assert.Equal(new[] { "CGY", "DEN", "EDM" }, vocabulary.Sites.Order(StringComparer.Ordinal));
        Assert.Equal(4, vocabulary.Types.Count);
        var cors = provider.GetRequiredService<IOptions<CorsOptions>>().Value;
        Assert.Equal(new[] { "GET" }, cors.GetPolicy(ReadCorsComposition.PolicyName)!.Methods);
        Assert.Equal(new[] { "POST" }, cors.GetPolicy(AiQueryApiComposition.CorsPolicyName)!.Methods);
        Assert.False(cors.GetPolicy(AiQueryApiComposition.CorsPolicyName)!.SupportsCredentials);
        // Resolving Application composition would require the intentionally absent generator; disabled discovery must not do so.
        Assert.Null(provider.GetService<IStructuredQueryGenerator>());
        var options = provider.GetRequiredService<AiQueryOptions>();
        Assert.False(options.Enabled); Assert.Null(options.ApiKey); Assert.Null(options.Profile); Assert.Null(options.BudgetCounterPath);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("private-config-canary")]
    [InlineData("")]
    [InlineData(" false ")]
    [InlineData("0")]
    [InlineData(null)]
    public void ExplicitInvalidOrPrematureEnablement_FailsWithoutEchoingTheValue(string? flag)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AiQueries:Enabled"] = flag }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAiQueryApi(configuration));
        Assert.Contains("AiQueries:Enabled", error.Message);
        Assert.DoesNotContain("private-config-canary", error.Message);
    }

    [Theory]
    [InlineData("AiQueries")]
    [InlineData("AiQueries:Enabled:child")]
    [InlineData("AiQueries:Enabled:0")]
    public void NonLeafConfiguration_IsRejectedWithoutInspectingOtherAiSettings(string key)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [key] = "private-config-canary", ["AiQueries:ApiKey"] = "not-read-by-this-composition" }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAiQueryApi(configuration));
        Assert.Contains("AiQueries:Enabled", error.Message);
        Assert.DoesNotContain("private-config-canary", error.Message);
        Assert.DoesNotContain("not-read-by-this-composition", error.Message);
    }
    /// <summary>Premature production enablement must fail before looking up active provider or counter settings.</summary>
    [Fact]
    public void EnabledGate_PrecedesActiveProviderAndCredentialValidation()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["AiQueries:Enabled"] = "true", ["AiQueries:Provider"] = "private-config-canary", ["AiQueries:ApiKey"] = "not-a-valid-key value" }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAiQueryApi(configuration));
        Assert.Contains("AiQueries:Enabled", error.Message);
        Assert.Contains("cannot be enabled", error.Message);
        Assert.DoesNotContain("private-config-canary", error.ToString());
        Assert.DoesNotContain("not-a-valid-key", error.ToString());
    }
    /// <summary>Proves a configuration reload cannot turn disabled registration into active credential binding.</summary>
    [Fact]
    public void EnablementChangesAfterFirstRead_StillRegistersOnlyDisabledSnapshot()
    {
        var guarded = new FlippingEnablementProvider();
        using var configuration = new ConfigurationRoot([guarded]);
        var services = new ServiceCollection();
        services.AddLogging(); services.AddReadApi(); services.AddReadCors(configuration); services.AddAiQueryApi(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<AiQueryOptions>().Enabled);
        Assert.Null(provider.GetRequiredService<AiQueryOptions>().ApiKey);
        Assert.Null(provider.GetService<IStructuredQueryGenerator>());
        Assert.Equal(1, guarded.EnabledReads); Assert.Equal(0, guarded.SensitiveReads);
    }

    /// <summary>Changes enablement on a second lookup and fails any active-setting value read.</summary>
    /// <remarks>Key metadata stays available; no real configuration or secret provider is involved.</remarks>
    private sealed class FlippingEnablementProvider : ConfigurationProvider
    {
        internal int EnabledReads { get; private set; }
        internal int SensitiveReads { get; private set; }
        internal FlippingEnablementProvider()
        { Data["AiQueries:Enabled"] = "false"; Data["AiQueries:ApiKey"] = "never-read-private-canary"; }
        public override bool TryGet(string key, out string? value)
        {
            if (key.Equals("AiQueries:Enabled", StringComparison.OrdinalIgnoreCase))
            { EnabledReads++; value = EnabledReads == 1 ? "false" : "true"; return true; }
            if (key.StartsWith("AiQueries:", StringComparison.OrdinalIgnoreCase))
            { SensitiveReads++; throw new InvalidOperationException("Sensitive setting must not be read."); }
            return base.TryGet(key, out value);
        }
    }
}
