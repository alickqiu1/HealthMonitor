using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HealthMonitor.IntegrationTests.Infrastructure;

namespace HealthMonitor.IntegrationTests.Api;

/// <summary>Four database-free real-Kestrel cases proving the local simulator consumes effective binding and actual socket facts.</summary>
/// <remarks>Feature-only fake-ingestion tests, not full Program, proxy/ingress, TLS, hosted access, or PostgreSQL proof.</remarks>
[Collection(SimulationEffectiveHostCollection.Name)]
public sealed class SimulationEffectiveHostTests
{
    [Fact]
    public Task Loopback_AllowsLocalPost_ClosesListener() => ExerciseAsync(KestrelSimulationTestScenario.BindingMode.Loopback, true);

    [Fact]
    public Task KestrelWildcardOverride_DeniesPost_ClosesListener() => ExerciseAsync(KestrelSimulationTestScenario.BindingMode.WildcardOverride, false);

    [Fact]
    public Task KestrelLoopbackOverride_AllowsPostDespiteWildcardUrl_ClosesListener() => ExerciseAsync(KestrelSimulationTestScenario.BindingMode.LoopbackOverride, true);

    /// <summary>Asserts all real feature entries and actual connection endpoints, including configured/effective binding disagreement.</summary>
    /// <param name="mode">URL configuration and Kestrel override combination.</param>
    /// <param name="allowed">Whether all effective bindings must be loopback and the write accepted.</param>
    /// <returns>Completion after listener closure and cleanup checks.</returns>
    private static async Task ExerciseAsync(KestrelSimulationTestScenario.BindingMode mode, bool allowed)
    {
        var scenario = new KestrelSimulationTestScenario(mode);
        await scenario.RunAsync(async test =>
        {
            Assert.Equal(test.ConfiguredUrl, test.ObservedConfiguredUrl); Assert.NotEmpty(test.EffectiveBindings); Assert.Equal(0, test.PublisherCount);
            Assert.All(test.EffectiveBindings, binding =>
            {
                Assert.True(binding.Port > 0); Assert.Equal(test.Address!.Port, binding.Port);
                Assert.True(IPAddress.TryParse(binding.Host.Trim('[', ']'), out var address));
                if (allowed) Assert.True(Loopback(address!));
                else Assert.True(address!.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any));
            });
            Guid batch = Guid.CreateVersion7();
            using var request = new HttpRequestMessage(HttpMethod.Post, SimulationTestHost.Route)
            { Content = JsonContent.Create(new { batchId = batch, deviceIds = new[] { Guid.NewGuid() }, scenario = "Healthy" }) };
            request.Headers.Host = "localhost"; request.Headers.Add("X-Forwarded-For", "127.0.0.1");
            using var response = await test.Client.SendAsync(request);
            Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (allowed)
            {
                Assert.Single(test.Ingestion.Requests); Assert.Equal(batch, json.RootElement.GetProperty("batchId").GetGuid());
                Assert.Equal(1, json.RootElement.GetProperty("affectedDeviceCount").GetInt32()); Assert.False(json.RootElement.GetProperty("replayed").GetBoolean());
            }
            else { Assert.Empty(test.Ingestion.Requests); Assert.Equal("simulation_local_only", json.RootElement.GetProperty("code").GetString()); }
            var connection = Assert.Single(test.Connections);
            Assert.NotNull(connection.Local); Assert.NotNull(connection.Remote);
            Assert.True(Loopback(connection.Local)); Assert.True(Loopback(connection.Remote));
            Assert.Equal(test.Address!.Port, connection.LocalPort); Assert.True(connection.RemotePort > 0);
        });
        Assert.True(scenario.Stopped); Assert.True(scenario.ListenerClosed); Assert.Empty(scenario.CleanupFailures);
    }

    [Fact]
    public async Task StartupAndBodyFailure_StillClosesListenerAndPreservesFailure()
    {
        foreach (bool startup in new[] { false, true })
        {
            var sentinel = new InvalidOperationException(startup ? "Intentional post-bind startup failure." : "Intentional body failure.");
            var scenario = new KestrelSimulationTestScenario(KestrelSimulationTestScenario.BindingMode.Loopback, startup ? sentinel : null);
            bool bodyRan = false;
            var error = await Record.ExceptionAsync(() => scenario.RunAsync(_ => { bodyRan = true; throw sentinel; }));
            Assert.NotNull(error);
            if (error is AggregateException aggregate) Assert.Contains(aggregate.Flatten().InnerExceptions, item => ReferenceEquals(item, sentinel));
            else Assert.Same(sentinel, error);
            Assert.Equal(!startup, bodyRan);
            if (startup) Assert.True(scenario.StartupFailureObservedBoundPort);
            Assert.Equal(startup, scenario.CleanupCapturedBindings); Assert.False(scenario.NoBoundListenerObserved);
            Assert.NotEmpty(scenario.EffectiveBindings); Assert.True(scenario.Address!.Port > 0);
            Assert.Empty(scenario.CleanupFailures); Assert.True(scenario.Stopped); Assert.True(scenario.ListenerClosed);
            // A second independent TCP probe is required after the original sentinel has been observed by this assertion scope.
            await scenario.VerifyClosedAsync();
        }
    }

    /// <summary>Normalizes mapped IPv4 only for asserting actual socket facts; no request header supplies these values.</summary>
    private static bool Loopback(IPAddress address) => IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}
