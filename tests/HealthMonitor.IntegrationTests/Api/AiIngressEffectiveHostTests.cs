using System.Net;
using System.Runtime.ExceptionServices;
using HealthMonitor.IntegrationTests.Infrastructure;

namespace HealthMonitor.IntegrationTests.Api;

/// <summary>Seven database-free real-Kestrel acceptance cases for captured identity and local AI admission.</summary>
/// <remarks>The controlled proxy peer is loopback, not a deployed proxy. Production activation, full Program,
/// HTTPS, real providers and hosted topology remain separate gates.</remarks>
[Collection(AiIngressEffectiveHostCollection.Name)]
public sealed class AiIngressEffectiveHostTests
{
    [Fact]
    public async Task DirectPeer_IgnoresSpoofedHeaders_ReusesClientWindow()
    {
        var scenario = new KestrelAiIngressTestScenario();
        await scenario.RunAsync(async test =>
        {
            for (int index = 1; index <= 6; index++)
                Assert.Equal(index <= 5 ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests,
                    await SendAsync(test, $"203.0.113.{index}"));
            Assert.Equal(5, test.Calls); Assert.Equal(5, test.Count());
            var requests = test.Observations.ToArray(); Assert.Equal(6, requests.Length);
            Assert.All(requests, request => Assert.False(string.IsNullOrWhiteSpace(request.Connection)));
            Assert.Single(requests.Select(request => request.Connection).Distinct());
            Assert.All(requests, request =>
            { Assert.Equal("127.0.0.1", request.RawPeer); Assert.Equal(request.RawPeer, request.Client); Assert.Equal("http", request.Scheme); });
        });
        Assert.True(scenario.ListenerClosed);
    }

    [Fact]
    public async Task KnownProxy_SymmetricHeaders_SelectVerifiedSnapshot()
    {
        var scenario = new KestrelAiIngressTestScenario { KnownProxy = "127.0.0.1" };
        await scenario.RunAsync(async test =>
        {
            Assert.Equal(HttpStatusCode.OK, await SendAsync(test, "203.0.113.9"));
            var request = Assert.Single(test.Observations);
            Assert.Equal("127.0.0.1", request.RawPeer); Assert.Equal("203.0.113.9", request.Client); Assert.Equal("https", request.Scheme);
            Assert.Equal(1, test.Calls); Assert.Equal(1, test.Count());
        });
        Assert.True(scenario.ListenerClosed);
    }

    [Fact]
    public async Task UnknownProxy_CannotSelectClientFromHeaders()
    {
        var scenario = new KestrelAiIngressTestScenario { KnownProxy = "192.0.2.1" };
        await scenario.RunAsync(async test =>
        {
            Assert.Equal(HttpStatusCode.OK, await SendAsync(test, "203.0.113.9"));
            var request = Assert.Single(test.Observations);
            Assert.Equal("127.0.0.1", request.RawPeer); Assert.Equal("127.0.0.1", request.Client); Assert.Equal("http", request.Scheme);
            Assert.Equal(1, test.Calls); Assert.Equal(1, test.Count());
        });
        Assert.True(scenario.ListenerClosed);
    }

    [Fact]
    public async Task MultiplexedHttp2_OneConnection_PreservesOverlappingRequestIdentities()
    {
        var scenario = new KestrelAiIngressTestScenario { Http2 = true, KnownProxy = "127.0.0.1", HoldProviders = true };
        await scenario.RunAsync(async test =>
        {
            var first = SendAsync(test, "203.0.113.9"); var second = SendAsync(test, "203.0.113.10");
            Exception? failure = null;
            try
            {
                await test.BothProvidersStarted.WaitAsync(TimeSpan.FromSeconds(5));
                // Both real requests reached the provider before either completed: serialization cannot satisfy this check.
                Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
                var requests = test.Observations.ToArray(); Assert.Equal(2, requests.Length);
                Assert.All(requests, request => Assert.False(string.IsNullOrWhiteSpace(request.Connection)));
                Assert.Single(requests.Select(request => request.Connection).Distinct());
                Assert.Equal(2, requests.Select(request => request.Stream).Distinct().Count());
                Assert.All(requests, request =>
                { Assert.Equal("HTTP/2", request.Protocol); Assert.True(request.Stream > 0); Assert.Equal("127.0.0.1", request.RawPeer); });
                Assert.Equal(new[] { "203.0.113.10", "203.0.113.9" }, requests.Select(request => request.Client).Order().ToArray());
                Assert.Equal(2, test.Count());
            }
            catch (Exception error) { failure = error; }
            finally
            {
                test.ReleaseProviders();
                // Drain establishes terminal states; explicitly observe client faults as well, preserving an earlier assertion.
                try
                {
                    await TelemetryIngestionTestScenario.DrainWritersAsync(first, second);
                    await Task.WhenAll(first, second);
                }
                catch (Exception drain)
                { failure = failure is null ? drain : new AggregateException("HTTP/2 assertion and client drain failed.", failure, drain); }
            }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.Equal(HttpStatusCode.OK, await first); Assert.Equal(HttpStatusCode.OK, await second); Assert.Equal(2, test.Calls);
        });
        Assert.True(scenario.ListenerClosed);
    }

    [Fact]
    public async Task MissingCaptureOrPriorRewrite_DeniesWithoutCharge_PreservesReads()
    {
        foreach (bool missing in new[] { true, false })
        {
            var scenario = new KestrelAiIngressTestScenario { OmitCapture = missing, RewritePeer = !missing };
            await scenario.RunAsync(async test =>
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, await SendAsync(test, "203.0.113.9"));
                using var read = await test.Client.GetAsync("/"); Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                Assert.Equal(0, test.Count()); Assert.Equal(0, test.Calls);
                Assert.All(test.Observations, request => Assert.Null(request.Client));
                if (missing) Assert.All(test.Observations, request => Assert.Null(request.RawPeer));
                else Assert.All(test.Observations, request => Assert.Equal("127.0.0.1", request.RawPeer));
            });
            Assert.True(scenario.ListenerClosed);
        }
    }

    [Fact]
    public async Task AutomaticForwarding_TrueOrMalformed_RejectsBeforeBinding()
    {
        foreach (string value in new[] { "true", "malformed" })
        {
            var scenario = new KestrelAiIngressTestScenario { AutomaticForwarding = value };
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.RunAsync(_ => throw new InvalidOperationException("Body must not run.")));
            Assert.Contains("ForwardedHeaders_Enabled", failure.Message);
            Assert.True(scenario.NoBoundListenerObserved); Assert.Null(scenario.Address); Assert.Equal(0, scenario.Calls);
        }
    }

    [Fact]
    public async Task PostBindAndBodyFailure_CloseListener_PreserveOriginalFailure()
    {
        foreach (bool startup in new[] { true, false })
        {
            var sentinel = new InvalidOperationException(startup ? "Controlled post-bind failure." : "Controlled body failure.");
            var scenario = new KestrelAiIngressTestScenario { StartupFailure = startup ? sentinel : null };
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.RunAsync(_ => throw sentinel));
            Assert.Same(sentinel, failure); Assert.NotNull(scenario.Address); Assert.True(scenario.ListenerClosed);
        }
    }

    /// <summary>Sends only to the scenario's actual loopback listener with explicit exact-version policy and fully consumes the response.</summary>
    /// <param name="test">Owner of the local listener and client.</param>
    /// <param name="forwarded">Untrusted request header value, never fixture-supplied identity metadata.</param>
    /// <returns>The observed HTTP status after the response is disposed.</returns>
    private static async Task<HttpStatusCode> SendAsync(KestrelAiIngressTestScenario test, string forwarded)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/")
        { Version = test.Client.DefaultRequestVersion, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        request.Headers.Add("X-Forwarded-For", forwarded); request.Headers.Add("X-Forwarded-Proto", "https");
        using var response = await test.Client.SendAsync(request);
        Assert.Equal(test.Client.DefaultRequestVersion, response.Version);
        await response.Content.ReadAsByteArrayAsync();
        return response.StatusCode;
    }
}
