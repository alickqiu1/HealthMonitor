using System.Text;
using HealthMonitor.Api.AiQueries;
using Microsoft.Extensions.Configuration;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Checks explicit ingress configuration without registering middleware, loading secrets or making network calls.</summary>
public sealed class TrustedIngressOptionsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"TrustedIngress\":null}")]
    [InlineData("{\"TrustedIngress\":{}}")]
    [InlineData("{\"TrustedIngress\":{\"Enabled\":false}}")]
    [InlineData("{\"TrustedIngress\":{\"KnownProxies\":[]}}")]
    [InlineData("{\"TrustedIngress\":{\"KnownProxies\":null}}")]
    [InlineData("{\"TrustedIngress\":{\"KnownProxies\":{}}}")]
    [InlineData("{\"TrustedIngress\":{\"KnownProxies\":\"\"}}")]
    public void MissingOrCollapsedEmptySections_DefaultToNoTrust(string json)
    {
        var options = TrustedIngressOptions.FromConfiguration(Json(json));
        Assert.False(options.Enabled); Assert.Empty(options.KnownProxies); Assert.Equal(1, options.ForwardLimit);
    }

    [Fact]
    public void ExactAddresses_NormalizeAndSnapshotWithoutMutableAddressObjects()
    {
        var configuration = Memory(new() { ["Enabled"] = "TRUE", ["ForwardLimit"] = "1",
            ["KnownProxies:0"] = "192.0.2.1", ["KnownProxies:1"] = "2001:0DB8::1",
            ["KnownProxies:2"] = "::ffff:192.0.2.2", ["KnownProxies:3"] = "fe80::1%12" });
        var options = TrustedIngressOptions.FromConfiguration(configuration);
        configuration["TrustedIngress:KnownProxies:0"] = "198.51.100.1";
        Assert.True(options.Enabled); Assert.Equal(1, options.ForwardLimit);
        Assert.Equal(new[] { "192.0.2.1", "2001:db8::1", "192.0.2.2", "fe80::1%12" }, options.KnownProxies);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)options.KnownProxies)[0] = "198.51.100.1");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    public void Enabled_AcceptsExactListBoundaries(int count)
    {
        var values = Enabled();
        for (int index = 0; index < count; index++) values["KnownProxies:" + index] = "192.0.2." + (index + 1);
        Assert.Equal(count, TrustedIngressOptions.FromConfiguration(Memory(values)).KnownProxies.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void Enabled_RejectsOutsideListBoundaries(int count)
    {
        var values = Enabled();
        for (int index = 0; index < count; index++) values["KnownProxies:" + index] = "192.0.2." + (index + 1);
        Reject(Memory(values));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" true ")]
    [InlineData("0")]
    [InlineData("private-value-canary")]
    public void InvalidEnabledLeaf_FailsEvenWithoutOtherSettings(string? value) => Reject(Memory(new() { ["Enabled"] = value }));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData(" 1 ")]
    [InlineData("private-value-canary")]
    public void SuppliedUnsupportedHop_FailsEvenWhenDisabled(string? value) => Reject(Memory(new() { ["ForwardLimit"] = value }));

    [Fact]
    public void Enabled_RequiresExplicitHop()
    {
        var values = Enabled(); values.Remove("ForwardLimit"); values["KnownProxies:0"] = "192.0.2.1";
        Reject(Memory(values));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" 192.0.2.1")]
    [InlineData("192.0.2.1 ")]
    [InlineData("proxy.example")]
    [InlineData("https://192.0.2.1")]
    [InlineData("192.0.2.0/24")]
    [InlineData("[::1]")]
    [InlineData("*")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("::%1")]
    [InlineData("255.255.255.255")]
    [InlineData("::ffff:0.0.0.0")]
    [InlineData("::ffff:255.255.255.255")]
    [InlineData("127.1")]
    [InlineData("2130706433")]
    [InlineData("0x7f000001")]
    [InlineData("192.000.2.1")]
    [InlineData("::ffff:192.000.2.1")]
    [InlineData("fe80::1%eth0")]
    [InlineData("fe80::1%01")]
    [InlineData("fe80::1%-1")]
    [InlineData("fe80::1%4294967296")]
    [InlineData("private-value-canary")]
    public void InvalidExactProxy_FailsEvenWhenDisabled(string? value) => Reject(Memory(new() { ["KnownProxies:0"] = value }));

    [Theory]
    [InlineData("192.0.2.1", "::ffff:192.0.2.1")]
    [InlineData("2001:db8::1", "2001:0DB8::0001")]
    [InlineData("fe80::1%12", "fe80::1%12")]
    public void DuplicateCanonicalPeer_IsRejected(string first, string second) => Reject(Memory(new()
        { ["KnownProxies:0"] = first, ["KnownProxies:1"] = second }));

    [Fact]
    public void NativeIpv6Scopes_RemainDistinct() => Assert.Equal(2, TrustedIngressOptions.FromConfiguration(Memory(new()
        { ["KnownProxies:0"] = "fe80::1%12", ["KnownProxies:1"] = "fe80::1%13" })).KnownProxies.Count);

    [Theory]
    [InlineData("{\"TrustedIngress\":\"private-value-canary\"}")]
    [InlineData("{\"TrustedIngress\":{\"private-key-canary\":true}}")]
    [InlineData("{\"TrustedIngress\":{\"Enabled\":{\"nested\":false}}}")]
    [InlineData("{\"TrustedIngress\":{\"Enabled\":null}}")]
    [InlineData("{\"TrustedIngress\":{\"ForwardLimit\":null}}")]
    [InlineData("{\"TrustedIngress\":{\"ForwardLimit\":[1]}}")]
    [InlineData("{\"TrustedIngress\":{\"KnownProxies\":\"192.0.2.1\"}}")]
    [InlineData("{\"TrustedIngress\":{\"KnownProxies\":[null]}}")]
    [InlineData("{\"TrustedIngress\":{\"KnownProxies\":[{\"ip\":\"192.0.2.1\"}]}}")]
    [InlineData("{\"TrustedIngress\":{\"KnownProxies\":{\"1\":\"192.0.2.1\"}}}")]
    [InlineData("{\"TrustedIngress\":{\"KnownProxies\":{\"00\":\"192.0.2.1\"}}}")]
    [InlineData("{\"TrustedIngress\":{\"KnownProxies\":{\"name\":\"192.0.2.1\"}}}")]
    [InlineData("{\"TrustedIngress\":{\"Enabled\":true,\"ForwardLimit\":1,\"KnownProxies\":[]}}")]
    [InlineData("{\"TrustedIngress\":{\"Enabled\":true,\"ForwardLimit\":1,\"KnownProxies\":null}}")]
    [InlineData("{\"TrustedIngress\":{\"Enabled\":true,\"ForwardLimit\":1,\"KnownProxies\":{}}}")]
    public void JsonProvider_RejectsObservableInvalidShapes(string json) => Reject(Json(json));

    [Fact]
    public void NullDependency_IsRejected() => Assert.Throws<ArgumentNullException>(() => TrustedIngressOptions.FromConfiguration(null!));

    /// <summary>Builds only in-memory nonsecret ingress settings; no process environment or application settings are loaded.</summary>
    private static IConfigurationRoot Memory(Dictionary<string, string?> values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.Select(pair => new KeyValuePair<string, string?>("TrustedIngress:" + pair.Key, pair.Value))).Build();

    /// <summary>Uses the actual JSON provider so tests reflect shape information retained by IConfiguration.</summary>
    private static IConfigurationRoot Json(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    /// <summary>Supplies only enablement prerequisites, leaving each test to choose exact proxy entries.</summary>
    private static Dictionary<string, string?> Enabled() => new() { ["Enabled"] = "true", ["ForwardLimit"] = "1" };

    /// <summary>Requires owned-key diagnostics without either malformed values or unknown configuration key names.</summary>
    private static void Reject(IConfiguration configuration)
    {
        var error = Assert.Throws<InvalidOperationException>(() => TrustedIngressOptions.FromConfiguration(configuration));
        Assert.StartsWith("TrustedIngress", error.Message);
        Assert.DoesNotContain("private-value-canary", error.Message); Assert.DoesNotContain("private-key-canary", error.Message);
        Assert.Null(error.InnerException);
    }
}
