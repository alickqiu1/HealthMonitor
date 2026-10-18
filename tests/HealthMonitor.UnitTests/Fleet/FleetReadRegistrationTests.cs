using HealthMonitor.Api.Composition;
using HealthMonitor.Api.Fleet;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Infrastructure.Data.DeviceContext;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HealthMonitor.UnitTests.Fleet;

/// <summary>Checks fixed nonsecret configuration and scoped composition without starting a host or opening storage.</summary>
public sealed class FleetReadRegistrationTests
{
    [Fact]
    public void NullDependencies_RejectBeforeRegistration()
    {
        Assert.Throws<ArgumentNullException>(() => FleetReadServiceCollectionExtensions.AddFleetReads(null!, Configuration()));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddFleetReads(null!));
    }

    [Fact]
    public void MissingSettings_UseCanonicalRules_AndRegisterSystemClock()
    {
        var services = new ServiceCollection();
        Assert.Same(services, services.AddFleetReads(Configuration()));
        var clock = Assert.Single(services, d => d.ServiceType == typeof(TimeProvider));
        Assert.Same(TimeProvider.System, clock.ImplementationInstance);
        Assert.Equal(ServiceLifetime.Singleton, clock.Lifetime);
    }

    [Theory]
    [InlineData("Monitoring:OfflineAfterSeconds", "90")]
    [InlineData("Monitoring:LatencyWarningMs", "250")]
    [InlineData("Monitoring:CpuWarningPercent", "80")]
    [InlineData("Monitoring:CpuWarningPercent", "80.00")]
    public void SupportedValues_AreAccepted(string key, string value) =>
        new ServiceCollection().AddFleetReads(Configuration(key, value));

    [Theory]
    [InlineData("Monitoring:OfflineAfterSeconds", "89")]
    [InlineData("Monitoring:OfflineAfterSeconds", "91")]
    [InlineData("Monitoring:LatencyWarningMs", "249")]
    [InlineData("Monitoring:LatencyWarningMs", "251")]
    [InlineData("Monitoring:CpuWarningPercent", "79.99")]
    [InlineData("Monitoring:CpuWarningPercent", "80.01")]
    [InlineData("Monitoring:OfflineAfterSeconds", "")]
    [InlineData("Monitoring:LatencyWarningMs", "not-a-number")]
    [InlineData("Monitoring:CpuWarningPercent", "8,0")]
    [InlineData("Monitoring:CpuWarningPercent", "NaN")]
    public void UnsupportedValues_RejectWithOwnedKey_WithoutRegisteringServices(string key, string value)
    {
        var services = new ServiceCollection();
        var failure = Assert.Throws<InvalidOperationException>(() => services.AddFleetReads(Configuration(key, value)));
        Assert.Contains(key, failure.Message);
        Assert.Empty(services);
    }

    [Fact]
    public void ScopedReaders_UseExistingContextsAndClock_WithoutDatabaseAccess()
    {
        var services = new ServiceCollection();
        var clock = new FixedClock();
        services.AddSingleton<TimeProvider>(clock);
        services.AddDbContext<DeviceDbContext>();
        services.AddDbContext<TelemetryDbContext>();
        services.AddDeviceCatalog();
        services.AddFleetReads(Configuration());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var queries = first.ServiceProvider.GetRequiredService<IDeviceHealthQueries>();
        var reader = first.ServiceProvider.GetRequiredService<FleetReadService>();
        var detail = first.ServiceProvider.GetRequiredService<DeviceDetailReadService>();
        var history = first.ServiceProvider.GetRequiredService<DeviceHistoryReadService>();
        var historyQueries = first.ServiceProvider.GetRequiredService<IDeviceHistoryQueries>();
        Assert.IsType<DeviceHealthQueries>(queries);
        Assert.Same(queries, first.ServiceProvider.GetRequiredService<IDeviceHealthQueries>());
        Assert.NotSame(queries, second.ServiceProvider.GetRequiredService<IDeviceHealthQueries>());
        Assert.Same(reader, first.ServiceProvider.GetRequiredService<FleetReadService>());
        Assert.NotSame(reader, second.ServiceProvider.GetRequiredService<FleetReadService>());
        Assert.Same(detail, first.ServiceProvider.GetRequiredService<DeviceDetailReadService>());
        Assert.NotSame(detail, second.ServiceProvider.GetRequiredService<DeviceDetailReadService>());
        Assert.Same(history, first.ServiceProvider.GetRequiredService<DeviceHistoryReadService>());
        Assert.NotSame(history, second.ServiceProvider.GetRequiredService<DeviceHistoryReadService>());
        Assert.IsType<DeviceHistoryQueries>(historyQueries);
        Assert.Same(historyQueries, first.ServiceProvider.GetRequiredService<IDeviceHistoryQueries>());
        Assert.NotSame(historyQueries, second.ServiceProvider.GetRequiredService<IDeviceHistoryQueries>());
        Assert.Same(clock, first.ServiceProvider.GetRequiredService<TimeProvider>());
        Assert.Single(services, d => d.ServiceType == typeof(TimeProvider));
        Assert.Single(services, d => d.ServiceType == typeof(DeviceDbContext));
        Assert.Single(services, d => d.ServiceType == typeof(TelemetryDbContext));
        Assert.All(services.Where(d => d.ServiceType == typeof(IDeviceHealthQueries) || d.ServiceType == typeof(FleetReadService) || d.ServiceType == typeof(DeviceDetailReadService) || d.ServiceType == typeof(DeviceHistoryReadService) || d.ServiceType == typeof(IDeviceHistoryQueries)),
            d => Assert.Equal(ServiceLifetime.Scoped, d.Lifetime));
    }

    /// <summary>Builds only the selected nonsecret override; missing keys exercise canonical defaults.</summary>
    private static IConfiguration Configuration(string? key = null, string? value = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(key is null ? new Dictionary<string, string?>() :
            new Dictionary<string, string?> { [key] = value }).Build();

    /// <summary>Provides a distinct DI identity without wall-clock dependencies or timers.</summary>
    private sealed class FixedClock : TimeProvider;
}
