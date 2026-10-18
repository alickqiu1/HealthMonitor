using HealthMonitor.Api.Composition;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HealthMonitor.UnitTests.Configuration;

/// <summary>Checks the browser allowlist contract without host startup, secrets, DNS, or database access.</summary>
public sealed class CorsConfigurationTests
{
    /// <summary>Builds only explicit test configuration and resolves the named framework policy.</summary>
    private static CorsPolicy Policy(params string?[] origins)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(origins.Select((value, index) =>
            new KeyValuePair<string, string?>("Cors:AllowedOrigins:" + index, value))).Build();
        return Policy(config);
    }

    /// <summary>Resolves the policy from explicit configuration without creating any hosted services.</summary>
    private static CorsPolicy Policy(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddReadCors(config);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy(ReadCorsComposition.PolicyName)!;
    }

    [Theory]
    [InlineData("http://localhost:5173", "http://localhost:5173")]
    [InlineData("HTTPS://Example.COM:443", "https://example.com")]
    [InlineData("http://example.com:80", "http://example.com")]
    [InlineData("https://example.com:8443", "https://example.com:8443")]
    [InlineData("http://[::1]:5173", "http://[::1]:5173")]
    [InlineData("https://[::1]:443", "https://[::1]")]
    public void ValidOrigins_AreCanonicalExactAndReadOnly(string input, string expected)
    {
        var policy = Policy(input);
        Assert.Equal(expected, Assert.Single(policy.Origins));
        Assert.True(policy.IsOriginAllowed(expected));
        Assert.Equal("GET", Assert.Single(policy.Methods));
        Assert.Equal("Content-Type", Assert.Single(policy.Headers));
        Assert.Empty(policy.ExposedHeaders);
        Assert.False(policy.SupportsCredentials);
        Assert.False(policy.AllowAnyOrigin); Assert.False(policy.AllowAnyMethod); Assert.False(policy.AllowAnyHeader);
    }

    [Fact]
    public void EquivalentOrigins_AreDeduplicated()
    {
        Assert.Single(Policy("https://EXAMPLE.com:443", "https://example.com").Origins);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Cors\":{\"AllowedOrigins\":[]}}")]
    public void MissingOrEmptyList_DeniesAllOrigins(string json)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var policy = Policy(new ConfigurationBuilder().AddJsonStream(stream).Build());
        Assert.Empty(policy.Origins);
        Assert.False(policy.IsOriginAllowed("http://localhost:5173"));
        Assert.False(policy.IsOriginAllowed("null"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("null")]
    [InlineData("*")]
    [InlineData("https://*.example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("https://user:canary@example.com")]
    [InlineData("https://example.com/")]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com?canary=1")]
    [InlineData("https://example.com#canary")]
    [InlineData(" https://example.com")]
    [InlineData("https://example.com ")]
    [InlineData("https://example.com\\path")]
    [InlineData("https://example.com:99999")]
    [InlineData("/relative")]
    public void InvalidEntry_FailsWithoutEchoingInput(string? input)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Policy(input));
        Assert.Contains("Cors:AllowedOrigins", error.Message);
        Assert.DoesNotContain("canary", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("unexpected")]
    [InlineData("1")]
    [InlineData("01")]
    [InlineData("-1")]
    public void NonIndexedOrSparseList_RejectsValidOriginValues(string key)
    {
        // A valid origin isolates list-shape validation from URI validation; object entries must not be adopted.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new[]
        {
            new KeyValuePair<string, string?>("Cors:AllowedOrigins:" + key, "https://example.com")
        }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddReadCors(configuration));
        Assert.Contains("Cors:AllowedOrigins", error.Message);
        Assert.DoesNotContain("example.com", error.ToString());
    }
    [Theory]
    [InlineData("Cors:AllowedOrigins")]
    [InlineData("Cors:AllowedOrigins:0:Nested")]
    public void InvalidListShape_FailsBeforeHostConstruction(string key)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new[]
        {
            new KeyValuePair<string, string?>(key, "canary")
        }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddReadCors(configuration));
        Assert.Contains("Cors:AllowedOrigins", error.Message);
        Assert.DoesNotContain("canary", error.ToString());
    }
}
