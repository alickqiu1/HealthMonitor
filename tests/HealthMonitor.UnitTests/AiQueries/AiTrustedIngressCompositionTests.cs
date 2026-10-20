using System.Net;
using HealthMonitor.Api.AiQueries;
using HealthMonitor.Api.Composition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Verifies the owned ingress component with real framework middleware and controlled transport contexts, not sockets.</summary>
/// <remarks>The separate temporary Kestrel probe established feasibility, not durable production-composition acceptance.</remarks>
public sealed class AiTrustedIngressCompositionTests
{
    private const string Canary = "fictional-private-canary";

    /// <summary>Builds fake active settings using lexical private paths, without looking up secrets or touching storage.</summary>
    private static ConfigurationRoot Config(string? proxy = null, params (string Key, string? Value)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["AiQueries:Enabled"] = "true", ["AiQueries:ApiKey"] = Canary,
            ["AiQueries:BudgetCounterPath"] = Path.Combine(Path.GetTempPath(), "hm-ingress-fictional", "private", "budget.json")
        };
        if (proxy is not null)
        {
            values["TrustedIngress:Enabled"] = "true"; values["TrustedIngress:ForwardLimit"] = "1";
            values["TrustedIngress:KnownProxies:0"] = proxy;
        }
        foreach (var (key, value) in extra) values[key] = value;
        return (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>Supplies host path context only; all paths are fictional and no directory/file is created.</summary>
    private static AiQueryOptions Active(IConfiguration configuration) => AiQueryOptions.FromConfiguration(configuration, new HostPaths());

    /// <summary>Creates separate HTTP and transport identities; only this test-owned feature represents the transport.</summary>
    private static DefaultHttpContext Context(string peer = "198.51.100.1", int port = 9000)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET"; context.Request.Scheme = "http"; context.Request.Host = new HostString("local.invalid");
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer); context.Connection.RemotePort = port;
        context.Features.Set(AiConnectionPeer.Capture(new IPEndPoint(IPAddress.Parse(peer), port)));
        return context;
    }

    /// <summary>Owns component services and a real ApplicationBuilder pipeline with no server, provider or counter.</summary>
    private static Harness Pipeline(IConfiguration configuration, RequestDelegate? next = null, bool widenGlobalOptions = false)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(Active(configuration));
        services.AddAiTrustedIngress(configuration);
        if (widenGlobalOptions) services.Configure<ForwardedHeadersOptions>(options =>
        { options.ForwardedHeaders = ForwardedHeaders.All; options.ForwardLimit = null; options.KnownIPNetworks.Clear(); options.KnownProxies.Clear(); });
        var provider = services.BuildServiceProvider();
        try
        {
            var app = new ApplicationBuilder(provider); app.UseAiTrustedIngress(configuration);
            app.Run(next ?? (_ => Task.CompletedTask)); return new Harness(provider, app.Build());
        }
        catch { provider.Dispose(); throw; }
    }

    [Fact]
    public void Disabled_DoesNotResolveIngressOrReadConfiguration()
    {
        using var config = new ConfigurationRoot([new RejectEveryLookup()]);
        var guard = new DisabledServicesOnly();
        var app = new ApplicationBuilder(guard); guard.Armed = true;
        Assert.Same(app, app.UseAiTrustedIngress(config));
        var services = new ServiceCollection(); services.AddSingleton(AiQueryOptions.Disabled); services.AddAiTrustedIngress(config);
        using var provider = services.BuildServiceProvider();
        Assert.Same(TrustedIngressOptions.Disabled, provider.GetRequiredService<TrustedIngressOptions>());
        Assert.Null(provider.GetService<AiConnectionPeer>()); Assert.Null(provider.GetService<AiClientIdentity>());
    }

    [Fact]
    public void RequiredArguments_AreRejectedBeforeComposition()
    {
        using var config = Config();
        using var provider = new ServiceCollection().AddSingleton(AiQueryOptions.Disabled).BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        Assert.Throws<ArgumentNullException>(() => AiTrustedIngressComposition.AddAiTrustedIngress(null!, config));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddAiTrustedIngress(null!));
        Assert.Throws<ArgumentNullException>(() => AiTrustedIngressComposition.UseAiTrustedIngress(null!, config));
        Assert.Throws<ArgumentNullException>(() => app.UseAiTrustedIngress(null!));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task KestrelOptions_InstallCaptureOnlyWhenEnabledWithoutBindingAListener(bool enabled)
    {
        using var config = Config(); var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton(enabled ? Active(config) : AiQueryOptions.Disabled); services.AddAiTrustedIngress(config);
        using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<IOptions<KestrelServerOptions>>().Value; server.ApplicationServices = provider;
        ListenOptions? endpoint = null;
        server.Listen(IPAddress.Loopback, 12345, options => { endpoint = options; options.Protocols = HttpProtocols.Http2; });
        Assert.Equal(12345, endpoint!.IPEndPoint!.Port); Assert.Equal(HttpProtocols.Http2, endpoint.Protocols);
        await using var connection = new DefaultConnectionContext { RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 9000) };
        AiConnectionPeer? captured = null;
        endpoint.Use(_ => context => { captured = context.Features.Get<AiConnectionPeer>(); return Task.CompletedTask; });
        await endpoint.Build()(connection);
        Assert.Equal(enabled, captured is not null);
        if (enabled) Assert.True(captured!.Matches(Context("127.0.0.1")));
    }

    [Theory]
    [InlineData(false, "198.51.100.1", "198.51.100.1", "http")]
    [InlineData(true, "198.51.100.1", "203.0.113.9", "https")]
    [InlineData(true, "198.51.100.2", "198.51.100.2", "http")]
    [InlineData(true, "::ffff:198.51.100.1", "203.0.113.9", "https")]
    public async Task Forwarding_RequiresExplicitPeerAndLeavesHostPrefixUntouched(bool trusted, string peer, string expected, string scheme)
    {
        using var config = Config(trusted ? "198.51.100.1" : null);
        using var harness = Pipeline(config, context =>
        {
            Assert.Equal(scheme, context.Request.Scheme); // Observed before downstream redirect-dependent behavior.
            return Task.CompletedTask;
        });
        var context = Context(peer); context.Request.Headers["X-Forwarded-For"] = "203.0.113.9";
        context.Request.Headers["X-Forwarded-Proto"] = "https"; context.Request.Headers["X-Forwarded-Host"] = "attacker.invalid";
        context.Request.Headers["X-Forwarded-Prefix"] = "/attacker";
        await harness.Run(context);
        Assert.Equal(expected, context.Features.Get<AiClientIdentity>()!.GetAddress()!.ToString());
        Assert.Equal("local.invalid", context.Request.Host.Value); Assert.Empty(context.Request.PathBase.Value ?? "");
    }

    [Theory]
    [InlineData("missing")] [InlineData("null")] [InlineData("any")] [InlineData("broadcast")]
    [InlineData("zero-port")] [InlineData("non-ip")] [InlineData("prior-ip")] [InlineData("prior-port")]
    public async Task UnavailableTransport_DeniesIdentityButPreservesOrdinaryGet(string kind)
    {
        using var config = Config("198.51.100.1"); bool nextCalled = false;
        using var harness = Pipeline(config, _ => { nextCalled = true; return Task.CompletedTask; });
        var context = Context();
        switch (kind)
        {
            case "missing": context.Features.Set<AiConnectionPeer>(null); break;
            case "null": context.Features.Set(AiConnectionPeer.Capture(null)); break;
            case "any": context.Features.Set(AiConnectionPeer.Capture(new IPEndPoint(IPAddress.Any, 9000))); break;
            case "broadcast": context.Features.Set(AiConnectionPeer.Capture(new IPEndPoint(IPAddress.None, 9000))); break;
            case "zero-port": context.Features.Set(AiConnectionPeer.Capture(new IPEndPoint(IPAddress.Loopback, 0))); break;
            case "non-ip": context.Features.Set(AiConnectionPeer.Capture(new DnsEndPoint("must-not-resolve.invalid", 9000))); break;
            case "prior-ip": context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9"); break;
            case "prior-port": context.Connection.RemotePort = 1; break;
        }
        await harness.Run(context);
        Assert.True(nextCalled); Assert.Equal(200, context.Response.StatusCode);
        Assert.False(context.Features.Get<AiClientIdentity>()!.IsVerified);
    }

    [Theory]
    [InlineData(null)] [InlineData("false")] [InlineData("FALSE")]
    public async Task AutomaticForwardingOff_AllowsExplicitPipeline(string? flag)
    {
        using var config = Config(null, ("ForwardedHeaders_Enabled", flag)); using var harness = Pipeline(config);
        var context = Context(); await harness.Run(context); Assert.True(context.Features.Get<AiClientIdentity>()!.IsVerified);
    }

    [Theory]
    [InlineData("true")] [InlineData("TRUE")] [InlineData("")] [InlineData(" false ")]
    [InlineData("0")] [InlineData("fictional-private-canary")]
    public void AutomaticForwardingTrueOrMalformed_RejectsWithoutValues(string? value)
    {
        using var config = Config(null, ("ForwardedHeaders_Enabled", value));
        var error = Assert.Throws<InvalidOperationException>(() => Pipeline(config));
        Assert.Contains("ForwardedHeaders_Enabled", error.Message); Assert.DoesNotContain(Canary, error.ToString());
    }

    [Fact]
    public void NestedAutomaticForwarding_IsRejected()
    {
        using var config = Config(null, ("ForwardedHeaders_Enabled:child", Canary));
        Assert.Throws<InvalidOperationException>(() => Pipeline(config));
    }

    [Fact]
    public async Task BroadGlobalOptions_CannotWidenTheOwnedWhitelist()
    {
        using var config = Config("198.51.100.1"); using var harness = Pipeline(config, widenGlobalOptions: true);
        var context = Context("198.51.100.2"); context.Request.Headers["X-Forwarded-For"] = "203.0.113.9";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        await harness.Run(context); Assert.Equal("198.51.100.2", context.Features.Get<AiClientIdentity>()!.GetAddress()!.ToString());
    }

    [Theory]
    [InlineData("203.0.113.7, 203.0.113.9", "http, https", "203.0.113.9")]
    [InlineData("203.0.113.7, 203.0.113.9", "https", "198.51.100.1")]
    [InlineData("invalid", "https", "198.51.100.1")]
    public async Task ForwardLimitAndSymmetry_PreserveConservativePeer(string forwarded, string proto, string expected)
    {
        using var config = Config("198.51.100.1"); using var harness = Pipeline(config);
        var context = Context(); context.Request.Headers["X-Forwarded-For"] = forwarded; context.Request.Headers["X-Forwarded-Proto"] = proto;
        await harness.Run(context); Assert.Equal(expected, context.Features.Get<AiClientIdentity>()!.GetAddress()!.ToString());
    }

    [Fact]
    public async Task ScopedIpv6AndDetachedAddress_CannotMutateSnapshot()
    {
        using var config = Config(); using var harness = Pipeline(config); var context = Context("fe80::1%7");
        await harness.Run(context); var identity = context.Features.Get<AiClientIdentity>()!;
        identity.GetAddress()!.ScopeId = 8;
        Assert.Equal(7, identity.GetAddress()!.ScopeId);
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        Assert.Equal("fe80::1%7", identity.GetAddress()!.ToString());
        Assert.Null(AiClientIdentity.Capture(IPAddress.Parse("::%7")).GetAddress());
    }

    [Fact]
    public async Task ParallelRequestsSharingRawConnection_HaveIndependentClientSnapshots()
    {
        using var config = Config("198.51.100.1"); int arrived = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var harness = Pipeline(config, async _ =>
        { if (Interlocked.Increment(ref arrived) == 2) ready.TrySetResult(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); });
        var first = Context(); var second = Context(); var shared = first.Features.Get<AiConnectionPeer>(); second.Features.Set(shared);
        first.Request.Headers["X-Forwarded-For"] = "203.0.113.9"; second.Request.Headers["X-Forwarded-For"] = "203.0.113.10";
        first.Request.Headers["X-Forwarded-Proto"] = second.Request.Headers["X-Forwarded-Proto"] = "https";
        await Task.WhenAll(harness.Run(first), harness.Run(second)).WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Equal("203.0.113.9", first.Features.Get<AiClientIdentity>()!.GetAddress()!.ToString());
        Assert.Equal("203.0.113.10", second.Features.Get<AiClientIdentity>()!.GetAddress()!.ToString());
        Assert.Same(shared, first.Features.Get<AiConnectionPeer>()); Assert.Same(shared, second.Features.Get<AiConnectionPeer>());
    }

    /// <summary>Owns only a database/provider-free component container and its request delegate.</summary>
    private sealed class Harness(ServiceProvider services, RequestDelegate run) : IDisposable
    { internal RequestDelegate Run { get; } = run; public void Dispose() => services.Dispose(); }

    /// <summary>Arms after ApplicationBuilder construction to detect only disabled ingress lookups, not framework constructor probes.</summary>
    private sealed class DisabledServicesOnly : IServiceProvider
    {
        internal bool Armed { get; set; }
        public object? GetService(Type type) => type == typeof(AiQueryOptions) ? AiQueryOptions.Disabled
            : !Armed ? null : throw new InvalidOperationException("Disabled lookup forbidden.");
    }

    /// <summary>Throws on value or metadata access; disabled registration must retain the reference without inspecting it.</summary>
    private sealed class RejectEveryLookup : ConfigurationProvider
    {
        public override bool TryGet(string key, out string? value) => throw new InvalidOperationException("Disabled configuration read forbidden.");
        public override IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath) => throw new InvalidOperationException("Disabled metadata read forbidden.");
    }

    /// <summary>Supplies lexical paths only; it does not claim filesystem ownership or provisioning.</summary>
    private sealed class HostPaths : IWebHostEnvironment
    {
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "hm-ingress-fictional", "host");
        public string WebRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "hm-ingress-fictional", "host", "wwwroot");
        public string EnvironmentName { get; set; } = "Development"; public string ApplicationName { get; set; } = "FictionalIngress";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}