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
}
