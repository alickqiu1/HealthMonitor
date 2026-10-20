using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using HealthMonitor.Api.AiQueries;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Verifies create-once provisioning on owned temporary files; Unix cases are explicitly filtered from Windows-local runs.</summary>
public sealed class AiBudgetProvisionerTests
{
    private const string Canary = "private-provisioning-canary";

    [Fact]
    public async Task FirstRun_WritesExactUtcStateAndSamplesClockOnce()
    {
        using var setup = new Setup();
        await setup.Create();
        using var document = JsonDocument.Parse(File.ReadAllText(setup.Path));
        var root = document.RootElement;
        Assert.Equal(3, root.EnumerateObject().Count()); Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("2026-10-19", root.GetProperty("day").GetString()); Assert.Equal(0, root.GetProperty("count").GetInt32());
        Assert.Equal(1, setup.Clock.Reads); setup.AssertUnrelatedPreserved();
    }

    [Fact]
    public async Task CreatedState_RealRuntimeCounterCanReserveIt()
    {
        using var setup = new Setup(); await setup.Create();
        await using var runtime = new AiDailyBudgetStore(setup.Path, setup.Clock);
        Assert.Equal(AiDailyBudgetStore.Outcome.Admitted, await runtime.TryReserveAsync());
        using var document = JsonDocument.Parse(File.ReadAllText(setup.Path));
        Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
        var before = File.ReadAllBytes(setup.Path);
        await setup.AssertSafeFailure(); Assert.Equal(before, File.ReadAllBytes(setup.Path));
    }

    [Theory]
    [InlineData("nonzero")] [InlineData("old")] [InlineData("partial")] [InlineData("corrupt")]
    public async Task ExistingState_IsNeverReadAdoptedOrReset(string kind)
    {
        using var setup = new Setup();
        string contents = kind switch
        {
            "nonzero" => "{\"version\":1,\"day\":\"2026-10-19\",\"count\":73}",
            "old" => "{\"version\":1,\"day\":\"2026-01-01\",\"count\":93}",
            "partial" => "{", _ => Canary
        };
        File.WriteAllText(setup.Path, contents); File.WriteAllText(setup.Path + ".lock", "stable-sidecar");
        var before = File.ReadAllBytes(setup.Path); var lockBefore = File.ReadAllBytes(setup.Path + ".lock");
        await setup.AssertSafeFailure();
        Assert.Equal(before, File.ReadAllBytes(setup.Path)); Assert.Equal(lockBefore, File.ReadAllBytes(setup.Path + ".lock"));
        setup.AssertUnrelatedPreserved();
    }

    [Fact]
    public async Task ActiveSidecarOwner_PreventsCreationWithoutChangingItsFile()
    {
        using var setup = new Setup();
        File.WriteAllText(setup.Path + ".lock", Canary);
        using (var active = File.Open(setup.Path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { await setup.AssertSafeFailure(); Assert.False(File.Exists(setup.Path)); Assert.Equal(0, setup.Clock.Reads); }
        Assert.Equal(Canary, File.ReadAllText(setup.Path + ".lock")); setup.AssertUnrelatedPreserved();
    }

    [Fact]
    public async Task PreCancelledEntry_CreatesNoArtifacts()
    {
        using var setup = new Setup(); using var abort = new CancellationTokenSource(); abort.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Create(abort.Token));
        Assert.Equal(abort.Token, error.CancellationToken); Assert.Equal(0, setup.Clock.Reads);
        Assert.False(File.Exists(setup.Path)); Assert.False(File.Exists(setup.Path + ".lock"));
    }

    [Fact]
    public async Task CancellationBeforeCounterCreation_RetainsStableSidecarAndReleasesOwnedHandle()
    {
        using var setup = new Setup(); using var abort = new CancellationTokenSource(); setup.Clock.Observe = abort.Cancel;
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Create(abort.Token));
        Assert.Equal(abort.Token, error.CancellationToken); Assert.False(File.Exists(setup.Path)); Assert.True(File.Exists(setup.Path + ".lock"));
        using var available = File.Open(setup.Path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("missing")] [InlineData("empty")] [InlineData("relative")]
    [InlineData("web-root")] [InlineData("missing-parent")] [InlineData("nested-leaf")]
    public async Task InvalidPathOrMissingParent_FailsWithoutCreation(string kind)
    {
        using var setup = new Setup();
        setup.Config["AiQueries:BudgetCounterPath"] = kind switch
        {
            "missing" => null, "empty" => "", "relative" => "relative.json",
            "web-root" => System.IO.Path.Combine(setup.Environment.WebRootPath, "counter.json"),
            "missing-parent" => System.IO.Path.Combine(setup.Root, "absent", "counter.json"), _ => setup.Path
        };
        if (kind == "nested-leaf") setup.Config["AiQueries:BudgetCounterPath:Child"] = Canary;
        await setup.AssertSafeFailure(); Assert.False(File.Exists(setup.Path)); Assert.False(File.Exists(setup.Path + ".lock"));
        setup.AssertUnrelatedPreserved();
    }

    [Fact]
    public async Task PathOnlyReader_DoesNotLookUpEnablementProviderOrCredentialValues()
    {
        using var setup = new Setup(); var guarded = new GuardedValues(setup.Path);
        using var config = new ConfigurationRoot([guarded]);
        await AiBudgetProvisioner.CreateAsync(config, setup.Environment, setup.Clock);
        Assert.NotEmpty(guarded.Keys); Assert.All(guarded.Keys, key => Assert.Equal("AiQueries:BudgetCounterPath", key));
        Assert.True(File.Exists(setup.Path));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task RequiredDependencies_AreValidatedBeforeIo(int missing)
    {
        using var setup = new Setup();
        await Assert.ThrowsAsync<ArgumentNullException>(() => AiBudgetProvisioner.CreateAsync(missing == 0 ? null! : setup.Config,
            missing == 1 ? null! : setup.Environment, missing == 2 ? null! : setup.Clock));
        Assert.False(File.Exists(setup.Path)); Assert.False(File.Exists(setup.Path + ".lock"));
    }

    [Fact]
    public async Task FileInAncestorPosition_IsPreservedWithoutCreatingDirectories()
    {
        using var setup = new Setup();
        setup.Config["AiQueries:BudgetCounterPath"] = System.IO.Path.Combine(setup.UnrelatedPath, "counter.json");
        await setup.AssertSafeFailure(); setup.AssertUnrelatedPreserved(); Assert.False(File.Exists(setup.Path));
    }

    [Fact, Trait("Platform", "Unix"), UnsupportedOSPlatform("windows")]
    public async Task UnixCreation_UsesOwnerOnlyPermissions()
    {
        using var setup = new Setup(); await setup.Create();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(setup.Path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(setup.Path + ".lock"));
    }

    [Fact, Trait("Platform", "Unix"), UnsupportedOSPlatform("windows")]
    public async Task SymbolicAncestor_IsRejectedBeyondImmediateParent()
    {
        using var setup = new Setup();
        string nested = System.IO.Path.Combine(setup.StateDirectory, "nested"); Directory.CreateDirectory(nested); setup.ExtraDirectory = nested;
        string alias = System.IO.Path.Combine(setup.Root, "alias"); Directory.CreateSymbolicLink(alias, setup.StateDirectory); setup.DirectoryLink = alias;
        setup.Config["AiQueries:BudgetCounterPath"] = System.IO.Path.Combine(alias, "nested", "counter.json");
        await setup.AssertSafeFailure();
        Assert.False(File.Exists(System.IO.Path.Combine(nested, "counter.json"))); setup.AssertUnrelatedPreserved();
    }

    [Fact, Trait("Platform", "Unix"), UnsupportedOSPlatform("windows")]
    public async Task SymbolicSidecar_ExistingOrBroken_IsNeverFollowed()
    {
        foreach (bool targetExists in new[] { true, false })
        {
            using var setup = new Setup();
            if (targetExists) File.WriteAllText(setup.TargetPath, Canary);
            File.CreateSymbolicLink(setup.Path + ".lock", setup.TargetPath);
            await setup.AssertSafeFailure(); Assert.False(File.Exists(setup.Path));
            Assert.Equal(setup.TargetPath, new FileInfo(setup.Path + ".lock").LinkTarget);
            if (targetExists) Assert.Equal(Canary, File.ReadAllText(setup.TargetPath)); else Assert.False(File.Exists(setup.TargetPath));
        }
    }

    [Fact, Trait("Platform", "Unix"), UnsupportedOSPlatform("windows")]
    public async Task ExistingCounterSymbolicLink_IsRejectedWithoutChangingTarget()
    {
        using var setup = new Setup(); File.WriteAllText(setup.TargetPath, Canary);
        File.CreateSymbolicLink(setup.Path, setup.TargetPath);
        await setup.AssertSafeFailure(); Assert.Equal(Canary, File.ReadAllText(setup.TargetPath));
        Assert.Equal(setup.TargetPath, new FileInfo(setup.Path).LinkTarget);
    }

    /// <summary>Provides one UTC-day sample and a cancellation boundary before any new counter leaf is created.</summary>
    private sealed class Clock : TimeProvider
    {
        internal int Reads; internal Action? Observe;
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() { Reads++; Observe?.Invoke(); return new(2026, 10, 19, 23, 59, 59, TimeSpan.Zero); }
    }

    /// <summary>Fails if targeted provisioning reads any synthetic credential or unrelated configuration value.</summary>
    private sealed class GuardedValues : ConfigurationProvider
    {
        internal ConcurrentQueue<string> Keys { get; } = new();
        internal GuardedValues(string path) => Data = new Dictionary<string, string?>
        { ["AiQueries:BudgetCounterPath"] = path, ["AiQueries:Enabled"] = "true", ["AiQueries:Provider"] = Canary,
            ["AiQueries:ApiKey"] = Canary, ["ConnectionStrings:HealthMonitor"] = Canary };
        /// <inheritdoc />
        public override bool TryGet(string key, out string? value)
        {
            Keys.Enqueue(key);
            if (key != "AiQueries:BudgetCounterPath") throw new InvalidOperationException(Canary);
            return base.TryGet(key, out value);
        }
    }

    /// <summary>Supplies explicit lexical host locations without starting an application or loading settings/secrets.</summary>
    private sealed class HostPaths(string root) : IWebHostEnvironment
    {
        public string ContentRootPath { get; set; } = System.IO.Path.Combine(root, "host");
        public string WebRootPath { get; set; } = System.IO.Path.Combine(root, "host", "wwwroot");
        public string ApplicationName { get; set; } = "ControlledProvisioning";
        public string EnvironmentName { get; set; } = "Development";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>Presecures only its own test directory and deletes only known leaves/links after helper handles have terminated.</summary>
    private sealed class Setup : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HealthMonitor-budget-init-" + Guid.NewGuid().ToString("N"));
        internal string StateDirectory => System.IO.Path.Combine(Root, "private");
        internal string Path => System.IO.Path.Combine(StateDirectory, "counter.json");
        internal string TargetPath => System.IO.Path.Combine(Root, "target");
        internal string UnrelatedPath => System.IO.Path.Combine(Root, "unrelated.txt");
        internal string? DirectoryLink, ExtraDirectory;
        internal Clock Clock { get; } = new();
        internal HostPaths Environment { get; }
        internal ConfigurationRoot Config { get; }
        internal Setup()
        {
            Directory.CreateDirectory(StateDirectory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(StateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(UnrelatedPath, Canary); Environment = new(Root);
            Config = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AiQueries:BudgetCounterPath"] = Path }).Build();
        }
        /// <summary>Runs only the helper on the fixture's synthetic configuration; never invokes Program.</summary>
        internal Task Create(CancellationToken token = default) => AiBudgetProvisioner.CreateAsync(Config, Environment, Clock, token);
        /// <summary>Requires a fixed safe diagnostic without inner path, credential or file-content exceptions.</summary>
        internal async Task AssertSafeFailure()
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create());
            Assert.Null(error.InnerException); Assert.Contains("AiQueries:BudgetCounterPath", error.Message);
            Assert.DoesNotContain(Canary, error.Message); Assert.DoesNotContain(Root, error.Message);
        }
        /// <summary>Independently verifies preservation outside the selected counter/sidecar.</summary>
        internal void AssertUnrelatedPreserved() => Assert.Equal(Canary, File.ReadAllText(UnrelatedPath));
        /// <summary>Resolves the owned root before deleting explicit files; never follows a directory link recursively.</summary>
        public void Dispose()
        {
            Config.Dispose();
            string resolved = System.IO.Path.GetFullPath(Root);
            string parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())) + System.IO.Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(resolved).StartsWith("HealthMonitor-budget-init-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe provisioning-test cleanup path.");
            if (DirectoryLink is not null) Directory.Delete(DirectoryLink);
            File.Delete(Path); File.Delete(Path + ".lock"); File.Delete(TargetPath); File.Delete(UnrelatedPath);
            if (ExtraDirectory is not null) Directory.Delete(ExtraDirectory);
            Directory.Delete(StateDirectory); Directory.Delete(Root);
        }
    }
}
