using System.Net;
using HealthMonitor.Api.AiQueries;
using HealthMonitor.Api.Composition;
using HealthMonitor.Application.AiQueries;
using HealthMonitor.Application.AiQueries.Ports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Verifies real DI ownership and scoped identity without enabling production routing or calling a provider.</summary>
public sealed class AiQueryRuntimeCompositionTests
{
    [Theory]
    [InlineData("Groq")] [InlineData("OpenAI")]
    public async Task Snapshot_IsCapturedOnceAndFactoryIsLazy(string profile)
    {
        await using var setup = new Setup(profile);
        setup.Config["AiQueries:Provider"] = "invalid-after-snapshot"; setup.Config["AiQueries:ApiKey"] = "invalid after snapshot";
        Assert.Equal(0, setup.Factories); Assert.False(File.Exists(setup.Path + ".lock"));
        await using var scope = setup.Services.CreateAsyncScope();
        setup.Context(); var port = scope.ServiceProvider.GetRequiredService<IStructuredQueryGenerator>();
        Assert.Equal(1, setup.Factories); Assert.Same(setup.Options, setup.Captured); Assert.Equal(profile, setup.Captured!.Profile!.Name);
        Assert.False(File.Exists(setup.Path + ".lock"));
        await port.GenerateAsync("devices", setup.Vocabulary, default); Assert.Equal(1, setup.Provider.Calls);
        Assert.Same(setup.Clock, setup.Services.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public async Task NoHttpContext_FailsBeforeSingletonCreation()
    {
        await using var setup = new Setup(); await using var scope = setup.Services.CreateAsyncScope();
        setup.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        var error = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IStructuredQueryGenerator>());
        Assert.Equal("AI translation requires an active request.", error.Message); Assert.Equal(0, setup.Factories);
        Assert.False(File.Exists(setup.Path + ".lock"));
    }

    [Fact]
    public async Task ScopedContexts_RemainDistinctOverOneSharedOwner()
    {
        await using var setup = new Setup();
        await using var first = setup.Services.CreateAsyncScope(); setup.Context("192.0.2.1");
        var a = first.ServiceProvider.GetRequiredService<IStructuredQueryGenerator>();
        await using var second = setup.Services.CreateAsyncScope(); setup.Context("192.0.2.2");
        var b = second.ServiceProvider.GetRequiredService<IStructuredQueryGenerator>();
        Assert.NotSame(a, b); Assert.Same(first.ServiceProvider.GetRequiredService<AiQueryRuntime>(), second.ServiceProvider.GetRequiredService<AiQueryRuntime>());
        for (int index = 0; index < 5; index++) await a.GenerateAsync("devices", setup.Vocabulary, default);
        var denied = await Assert.ThrowsAsync<AiAdmissionRejectedException>(() => a.GenerateAsync("devices", setup.Vocabulary, default));
        Assert.Equal(AiAdmissionRejectedException.Rejection.RateLimited, denied.Reason);
        await b.GenerateAsync("devices", setup.Vocabulary, default); Assert.Equal(6, setup.Provider.Calls); Assert.Equal(1, setup.Factories);
    }

    [Fact]
    public async Task MissingMarker_LeavesCounterUntouched()
    {
        await using var setup = new Setup(); await using var scope = setup.Services.CreateAsyncScope(); setup.Context(verified: false);
        var port = scope.ServiceProvider.GetRequiredService<IStructuredQueryGenerator>();
        var error = await Assert.ThrowsAsync<AiAdmissionRejectedException>(() => port.GenerateAsync("devices", setup.Vocabulary, default));
        Assert.Equal(AiAdmissionRejectedException.Rejection.AdmissionUnavailable, error.Reason);
        Assert.Equal(0, setup.Provider.Calls); Assert.False(File.Exists(setup.Path + ".lock"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingOrCorruptCounter_FailsClosedWithoutBootstrap(bool corrupt)
    {
        await using var setup = new Setup();
        if (corrupt) File.WriteAllText(setup.Path, "private-counter-canary"); else File.Delete(setup.Path);
        await using var scope = setup.Services.CreateAsyncScope(); setup.Context();
        var port = scope.ServiceProvider.GetRequiredService<IStructuredQueryGenerator>();
        var error = await Assert.ThrowsAsync<AiAdmissionRejectedException>(() => port.GenerateAsync("devices", setup.Vocabulary, default));
        Assert.Equal(AiAdmissionRejectedException.Rejection.BudgetUnavailable, error.Reason); Assert.Equal(0, setup.Provider.Calls);
        if (corrupt) Assert.Equal("private-counter-canary", File.ReadAllText(setup.Path)); else Assert.False(File.Exists(setup.Path));
    }

    [Fact]
    public async Task AsyncDiDisposal_DisposesOnlyOneOwnerAndStopsBorrowedPorts()
    {
        await using var setup = new Setup(); await using var scope = setup.Services.CreateAsyncScope(); setup.Context();
        var port = scope.ServiceProvider.GetRequiredService<IStructuredQueryGenerator>();
        using var caller = new CancellationTokenSource(); await port.GenerateAsync("devices", setup.Vocabulary, caller.Token);
        Assert.Equal(caller.Token, setup.Provider.Token); Assert.Same(setup.Vocabulary, setup.Provider.Vocabulary);
        var owner = setup.Services.GetRequiredService<AiQueryRuntime>();
        await setup.Services.DisposeAsync(); await owner.CleanupCompletion;
        Assert.Equal(1, setup.Provider.Disposals);
        await Assert.ThrowsAsync<AiAdmissionRejectedException>(() => port.GenerateAsync("devices", setup.Vocabulary, default));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FactoryFailure_UsesSafeDiagnosticWithoutCounterIo(bool nonDisposable)
    {
        await using var setup = new Setup(factoryFailure: true, nonDisposable: nonDisposable); setup.Context();
        await using var scope = setup.Services.CreateAsyncScope();
        var error = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IStructuredQueryGenerator>());
        Assert.Equal("AI runtime initialization failed.", error.Message); Assert.Null(error.InnerException);
        Assert.False(File.Exists(setup.Path + ".lock"));
    }

    [Fact]
    public void InvalidRegistrationArguments_DoNotMutateServices()
    {
        var services = new ServiceCollection(); int before = services.Count;
        Assert.Throws<InvalidOperationException>(() => services.AddAiQueryRuntime(AiQueryOptions.Disabled));
        Assert.Throws<ArgumentNullException>(() => services.AddAiQueryRuntime(null!));
        Assert.Throws<ArgumentNullException>(() => AiQueryRuntimeComposition.AddAiQueryRuntime(null!, AiQueryOptions.Disabled));
        Assert.Throws<ArgumentNullException>(() => services.AddAiQueryRuntime(AiQueryOptions.Disabled, null!));
        Assert.Equal(before, services.Count);
    }

    [Fact]
    public async Task UnpublishedConcreteCounter_DisposalIsSynchronous()
    {
        await using var setup = new Setup(); var counter = new AiDailyBudgetStore(setup.Path, setup.Clock);
        var cleanup = counter.DisposeAsync(); Assert.True(cleanup.IsCompletedSuccessfully); await cleanup;
        Assert.False(File.Exists(setup.Path + ".lock"));
    }

    [Fact]
    public async Task DefaultFixedProvider_ConstructionDoesNotSendBeforeIdentityGate()
    {
        await using var setup = new Setup(useDefault: true); setup.Context(verified: false);
        await using var scope = setup.Services.CreateAsyncScope();
        var port = scope.ServiceProvider.GetRequiredService<IStructuredQueryGenerator>();
        await Assert.ThrowsAsync<AiAdmissionRejectedException>(() => port.GenerateAsync("devices", setup.Vocabulary, default));
        Assert.False(File.Exists(setup.Path + ".lock")); // Inner real provider Generate is unreachable; no network is permitted in this test.
    }

    /// <summary>Supplies a deterministic shared UTC day for the actual reservation store.</summary>
    private sealed class Clock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero); }

    /// <summary>Returns untrusted proposals while recording forwarding and exact disposal ownership.</summary>
    private sealed class Provider : IStructuredQueryGenerator, IDisposable
    {
        internal int Calls, Disposals; internal CancellationToken Token; internal QueryVocabulary? Vocabulary;
        public Task<StructuredQueryProposal?> GenerateAsync(string prompt, QueryVocabulary vocabulary, CancellationToken cancellationToken)
        { Calls++; Token = cancellationToken; Vocabulary = vocabulary; return Task.FromResult<StructuredQueryProposal?>(null); }
        public void Dispose() => Disposals++;
    }

    /// <summary>Exercises rejection before a non-disposable port can acquire any owned policy/counter resources.</summary>
    private sealed class NonDisposable : IStructuredQueryGenerator
    { public Task<StructuredQueryProposal?> GenerateAsync(string prompt, QueryVocabulary vocabulary, CancellationToken cancellationToken) => Task.FromResult<StructuredQueryProposal?>(null); }

    /// <summary>Provides only lexical hosting paths; no configuration providers beyond synthetic memory are loaded.</summary>
    private sealed class Paths(string root) : IWebHostEnvironment
    {
        public string ContentRootPath { get; set; } = System.IO.Path.Combine(root, "host");
        public string WebRootPath { get; set; } = System.IO.Path.Combine(root, "host", "wwwroot");
        public string EnvironmentName { get; set; } = "Development"; public string ApplicationName { get; set; } = "ControlledComposition";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider(); public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>Owns only a private fixture directory and async DI container; deletion follows actual runtime cleanup.</summary>
    private sealed class Setup : IAsyncDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HealthMonitor-runtime-di-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(root, "counter.json");
        internal Clock Clock { get; } = new(); internal Provider Provider { get; } = new();
        internal ServiceProvider Services { get; } internal ConfigurationRoot Config { get; } internal AiQueryOptions Options { get; }
        internal AiQueryOptions? Captured; internal int Factories;
        internal QueryVocabulary Vocabulary { get; } = new(["DEN"], ["Server"]);
        internal Setup(string profile = "Groq", bool factoryFailure = false, bool nonDisposable = false, bool useDefault = false)
        {
            Directory.CreateDirectory(root); File.WriteAllText(Path, "{\"version\":1,\"day\":\"2026-10-19\",\"count\":0}");
            Config = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["AiQueries:Enabled"] = "true", ["AiQueries:Provider"] = profile, ["AiQueries:ApiKey"] = "synthetic-key", ["AiQueries:BudgetCounterPath"] = Path }).Build();
            Options = AiQueryOptions.FromConfiguration(Config, new Paths(root));
            var services = new ServiceCollection(); services.AddSingleton<TimeProvider>(Clock);
            if (useDefault) services.AddAiQueryRuntime(Options);
            else services.AddAiQueryRuntime(Options, snapshot =>
            { Factories++; Captured = snapshot; if (factoryFailure) return nonDisposable ? new NonDisposable() : throw new InvalidOperationException("private-factory-canary"); return Provider; });
            Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        }
        /// <summary>Supplies explicit synthetic request identity only to the current scoped port.</summary>
        internal void Context(string address = "192.0.2.1", bool verified = true)
        {
            var context = new DefaultHttpContext(); if (verified) context.Features.Set(AiClientIdentity.Capture(IPAddress.Parse(address)));
            Services.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        }
        /// <summary>Container async disposal drains its owner; failed factory construction has no published work to drain.</summary>
        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync(); Config.Dispose();
            string resolved = System.IO.Path.GetFullPath(root);
            string parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())) + System.IO.Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(resolved).StartsWith("HealthMonitor-runtime-di-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe composition cleanup path.");
            File.Delete(Path); File.Delete(Path + ".lock"); Directory.Delete(root);
        }
    }
}
