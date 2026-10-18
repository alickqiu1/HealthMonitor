using System.Net;
using HealthMonitor.Api.Simulation;
using Microsoft.Extensions.Configuration;

namespace HealthMonitor.UnitTests.Configuration;

/// <summary>Checks local opt-in and pure effective-binding facts without claiming real server binding or opening any socket.</summary>
public sealed class SimulationHttpTests
{
    /// <summary>Builds only the two fictional nonsecret flags under test.</summary>
    private static IConfiguration Config(string? enabled = null, string? anonymous = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Simulation:HttpEndpointEnabled"] = enabled, ["Simulation:AllowLocalAnonymous"] = anonymous }).Build();

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Testing")]
    public void Defaults_DisableSimulation(string environment) => Assert.False(SimulationHttpOptions.Bind(Config(), environment).Enabled);

    [Fact]
    public void ExplicitDevelopmentOptIn_EnablesOnlyStaticMode() =>
        Assert.True(SimulationHttpOptions.Bind(Config("true", "true"), "Development").Enabled);

    [Theory]
    [InlineData("true", "false", "Development")]
    [InlineData("true", null, "Development")]
    [InlineData("true", "true", "Production")]
    [InlineData("true", "true", "Staging")]
    [InlineData("canary", "false", "Development")]
    [InlineData("false", "canary", "Development")]
    public void UnsupportedConfiguration_FailsWithoutEcho(string? enabled, string? anonymous, string environment)
    {
        var error = Assert.Throws<InvalidOperationException>(() => SimulationHttpOptions.Bind(Config(enabled, anonymous), environment));
        Assert.Contains("Simulation:", error.Message); Assert.DoesNotContain("canary", error.ToString()); Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("https://[::1]:5001")]
    public void LoopbackBindingsAndConnections_AreAllowed(string binding) => Assert.True(LocalSimulationAccess.IsAllowed(
        true, new[] { binding }, IPAddress.IPv6Loopback, IPAddress.Parse("::ffff:127.0.0.1")));

    [Theory]
    [InlineData("http://0.0.0.0:5000")]
    [InlineData("http://[::]:5000")]
    [InlineData("http://*:5000")]
    [InlineData("http://+:5000")]
    [InlineData("http://example.com:5000")]
    [InlineData("http://localhost.evil:5000")]
    [InlineData("http://localhost:5000/path")]
    [InlineData("unix:/tmp/socket")]
    [InlineData("")]
    public void UnknownWildcardOrMixedBindings_AreDenied(string binding) => Assert.False(LocalSimulationAccess.IsAllowed(
        true, new[] { "http://localhost:5000", binding }, IPAddress.Loopback, IPAddress.Loopback));

    [Fact]
    public void MissingEmptyBindingOrMissingConnection_Denies()
    {
        Assert.False(LocalSimulationAccess.IsAllowed(true, null, IPAddress.Loopback, IPAddress.Loopback));
        Assert.False(LocalSimulationAccess.IsAllowed(true, Array.Empty<string>(), IPAddress.Loopback, IPAddress.Loopback));
        Assert.False(LocalSimulationAccess.IsAllowed(true, new[] { "http://localhost:5000" }, null, IPAddress.Loopback));
        Assert.False(LocalSimulationAccess.IsAllowed(true, new[] { "http://localhost:5000" }, IPAddress.Loopback, null));
    }

    [Theory]
    [InlineData("10.0.0.2", "127.0.0.1")]
    [InlineData("127.0.0.1", "10.0.0.2")]
    [InlineData("127.0.0.1", "::ffff:10.0.0.2")]
    public void ActualNonLoopbackConnection_Denies(string local, string remote) => Assert.False(LocalSimulationAccess.IsAllowed(
        true, new[] { "http://localhost:5000" }, IPAddress.Parse(local), IPAddress.Parse(remote)));

    [Fact]
    public void DisabledMode_DeniesEvenLocalFacts() => Assert.False(LocalSimulationAccess.IsAllowed(
        false, new[] { "http://localhost:5000" }, IPAddress.Loopback, IPAddress.Loopback));
}
