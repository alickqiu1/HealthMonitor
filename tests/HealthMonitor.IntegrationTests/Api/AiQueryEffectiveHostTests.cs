using System.Net;
using System.Text.Json;
using HealthMonitor.Application.AiQueries;
using HealthMonitor.IntegrationTests.Infrastructure;

namespace HealthMonitor.IntegrationTests.Api;

/// <summary>Eight real-connection acceptance cases for optional production MVC composition, using owned fake generation only.</summary>
/// <remarks>These exercise configured routes and local lifetimes, not Program/database workers, TLS deployment or live model quality.</remarks>
[Collection(AiIngressEffectiveHostCollection.Name)]
public sealed class AiQueryEffectiveHostTests
{
    [Fact]
    public async Task Disabled_AbsentRoute_NoActiveSettingReads()
    {
        await new KestrelAiApiTestScenario { Enabled = false }.RunAsync(async host =>
        {
            using var response = await host.SendAsync();
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(0, host.Calls); Assert.Equal(0, host.SensitiveReads); Assert.Equal(0, host.Count());
        });
    }

    [Fact]
    public async Task EnabledProfiles_ValidatedPreview_NoStoreAndSingleCharge()
    {
        foreach (string profile in new[] { "Groq", "OpenAI" })
        {
            var scenario = new KestrelAiApiTestScenario { Profile = profile };
            await scenario.RunAsync(async host =>
            {
                using var response = await host.SendAsync();
                Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal("Ready", document.RootElement.GetProperty("outcome").GetString());
                var query = document.RootElement.GetProperty("query");
                Assert.Equal("DEN", query.GetProperty("site").GetString());
                Assert.Equal("Server", query.GetProperty("type").GetString());
                Assert.Equal(1, host.Calls); Assert.Equal(1, host.Count());
            });
            Assert.Equal(1, scenario.ProviderDisposals);
        }
    }

    [Fact]
    public async Task InvalidRequests_NoAdmissionOrProvider()
    {
        await new KestrelAiApiTestScenario().RunAsync(async host =>
        {
            foreach (var item in new[] { ("{\"prompt\":\" \"}", 400), ("{", 400), ("{\"prompt\":\"" + new string('x', 8200) + "\"}", 413) })
            {
                using var response = await host.SendAsync(item.Item1);
                Assert.Equal(item.Item2, (int)response.StatusCode); NoStore(response);
            }
            Assert.Equal(0, host.Calls); Assert.Equal(0, host.Count());
        });
    }

    [Fact]
    public async Task MissingOrCorruptBudget_FailsClosedWithoutBootstrap()
    {
        foreach (string? contents in new[] { null, "corrupt-owned-test-counter" })
            await new KestrelAiApiTestScenario { BudgetContents = contents }.RunAsync(async host =>
            {
                using var response = await host.SendAsync();
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); NoStore(response);
                Assert.Equal(0, host.Calls);
                if (contents is null) Assert.False(File.Exists(host.CounterPath));
                else Assert.Equal(contents, File.ReadAllText(host.CounterPath));
            });
    }

    [Fact]
    public async Task EffectiveClientLimit_UsesTrustedSnapshot()
    {
        foreach (bool trusted in new[] { false, true })
        {
            var scenario = new KestrelAiApiTestScenario();
            scenario.Settings["TrustedIngress:Enabled"] = "true";
            scenario.Settings["TrustedIngress:ForwardLimit"] = "1";
            scenario.Settings["TrustedIngress:KnownProxies:0"] = trusted ? "127.0.0.1" : "192.0.2.200";
            await scenario.RunAsync(async host =>
            {
                for (int i = 0; i < 5; i++)
                { using var ok = await host.SendAsync(forwarded: "198.51.100.10"); Assert.Equal(HttpStatusCode.OK, ok.StatusCode); }
                using var limited = await host.SendAsync(forwarded: "198.51.100.10");
                Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); NoStore(limited);
                using var another = await host.SendAsync(forwarded: "198.51.100.11");
                Assert.Equal(trusted ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests, another.StatusCode);
                Assert.Equal(trusted ? 6 : 5, host.Calls); Assert.Equal(host.Calls, host.Count());
            });
        }
    }

    [Fact]
    public async Task Shutdown_DrainsUncooperativeRequestAndRetainsOwnership()
    {
        var scenario = new KestrelAiApiTestScenario { HoldProvider = true };
        await scenario.RunAsync(async host =>
        {
            var responseWork = host.SendAsync();
            await host.ProviderEntered.WaitAsync(TimeSpan.FromSeconds(5));
            var runtime = host.Runtime;
            var stopping = host.StopAsync();
            await host.HostStopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopping.IsCompleted); Assert.Equal(0, host.ProviderDisposals);
            Assert.Throws<IOException>(() => new FileStream(host.CounterPath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            host.ReleaseProvider();
            using var response = await responseWork;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            // Host Stop drains requests; asynchronous container disposal owns the runtime's final resource release.
            Assert.Equal(0, host.ProviderDisposals);
            await host.DisposeHostAsync(); await runtime.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, host.ProviderDisposals);
            using var released = new FileStream(host.CounterPath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
        Assert.Equal(1, scenario.ProviderDisposals);
    }

    [Fact]
    public async Task InvalidConfiguration_RejectsBeforeBinding()
    {
        foreach (var setting in new[] { ("AiQueries:ApiKey", ""), ("AiQueries:Provider", "private-profile-canary"),
            ("AiQueries:BudgetCounterPath", "relative.json"), ("ForwardedHeaders_Enabled", "true") })
        {
            var scenario = new KestrelAiApiTestScenario { ExpectStartupFailure = true };
            scenario.Settings[setting.Item1] = setting.Item2;
            await scenario.RunAsync(host =>
            {
                Assert.Null(host.Address); Assert.NotNull(host.StartupFailure);
                Assert.True(host.RejectedBeforeHostStart || host.NoBoundListenerObserved);
                Assert.DoesNotContain("private-profile-canary", host.StartupFailure.ToString());
                Assert.Equal(0, host.Calls); Assert.Equal(0, host.Count()); return Task.CompletedTask;
            });
        }
    }

    [Fact]
    public async Task ProviderFailures_AreSafeChargedAndDoNotRetry()
    {
        foreach (string profile in new[] { "Groq", "OpenAI" })
            foreach (bool timeout in new[] { false, true })
                await new KestrelAiApiTestScenario { Profile = profile,
                    ProviderFailure = timeout ? new AiProviderTimeoutException() : new AiProviderUnavailableException() }.RunAsync(async host =>
                {
                    using var response = await host.SendAsync("{\"prompt\":\"private-prompt-canary\"}");
                    Assert.Equal(timeout ? HttpStatusCode.GatewayTimeout : HttpStatusCode.ServiceUnavailable, response.StatusCode);
                    NoStore(response);
                    string body = await response.Content.ReadAsStringAsync();
                    Assert.DoesNotContain("private-prompt-canary", body); Assert.DoesNotContain("synthetic-key", body);
                    Assert.Equal(1, host.Calls); Assert.Equal(1, host.Count());
                });
    }

    /// <summary>Checks matched-route failures as well as successful previews cannot enter shared browser caches.</summary>
    private static void NoStore(HttpResponseMessage response) => Assert.True(response.Headers.CacheControl?.NoStore);
}
