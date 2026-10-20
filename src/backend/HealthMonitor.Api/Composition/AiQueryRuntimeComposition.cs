using HealthMonitor.Api.AiQueries;
using HealthMonitor.Application.AiQueries.Ports;
using HealthMonitor.Infrastructure.AiQueries;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HealthMonitor.Api.Composition;

/// <summary>Registers one lazy resource owner over an already validated enabled snapshot, without activating HTTP routes.</summary>
internal static class AiQueryRuntimeComposition
{
    /// <summary>Fixes production generation to the selected reviewed profile and backend-only credential.</summary>
    /// <param name="services">Services retaining the existing clock and logger configuration.</param>
    /// <param name="options">One immutable validated enabled snapshot; configuration is never rebound.</param>
    /// <returns>The same service collection, with only the runtime owning provider/policy/counter disposal.</returns>
    internal static IServiceCollection AddAiQueryRuntime(this IServiceCollection services, AiQueryOptions options) =>
        AddAiQueryRuntime(services, options, snapshot => new ChatCompletionsStructuredQueryGenerator(snapshot.Profile!, snapshot.ApiKey!));

    /// <summary>Permits controlled disposable ports in component tests; production uses the fixed overload.</summary>
    /// <param name="services">Services receiving lazy owner and request-scoped port registrations.</param>
    /// <param name="options">The already validated enabled snapshot, captured unchanged.</param>
    /// <param name="createProvider">Internal test seam; ownership transfers only when it returns a disposable port.
    /// A factory remains responsible for allocations it does not return.</param>
    /// <returns>The same collection; no provider call, counter read, lock acquisition or network occurs during registration.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The snapshot is disabled.</exception>
    internal static IServiceCollection AddAiQueryRuntime(this IServiceCollection services, AiQueryOptions options,
        Func<AiQueryOptions, IStructuredQueryGenerator> createProvider)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(createProvider);
        if (!options.Enabled) throw new InvalidOperationException("AI runtime registration requires an enabled validated snapshot.");
        services.AddLogging(); services.AddHttpContextAccessor(); services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton(provider => CreateOwner(provider, options, createProvider));
        services.AddScoped<IStructuredQueryGenerator>(provider =>
        {
            // Resolving a scoped port outside HTTP must not allocate the singleton provider or touch budget state.
            var context = provider.GetRequiredService<IHttpContextAccessor>().HttpContext
                ?? throw new InvalidOperationException("AI translation requires an active request.");
            return provider.GetRequiredService<AiQueryRuntime>().CreateGenerator(context);
        });
        return services;
    }

    /// <summary>Transfers unpublished resources together; DI must never dispose the inner resources independently.</summary>
    private static AiQueryRuntime CreateOwner(IServiceProvider services, AiQueryOptions options, Func<AiQueryOptions, IStructuredQueryGenerator> createProvider)
    {
        IDisposable? providerOwner = null; AiAdmissionPolicy? policy = null; AiDailyBudgetStore? counter = null;
        try
        {
            var clock = services.GetRequiredService<TimeProvider>();
            var logger = services.GetRequiredService<ILogger<AiQueryRuntime>>();
            var generator = createProvider(options);
            providerOwner = generator as IDisposable ?? throw new InvalidOperationException("Provider disposal ownership is required.");
            policy = new(); counter = new(options.BudgetCounterPath!, clock);
            return new(generator, policy, counter, clock, logger);
        }
        catch
        {
            bool cleanupFailed = false;
            try { providerOwner?.Dispose(); } catch { cleanupFailed = true; }
            try { policy?.Dispose(); } catch { cleanupFailed = true; }
            // This sealed store is unpublished and has performed no I/O; its concrete DisposeAsync completes synchronously.
            // The unit invariant guards this narrow recovery path; normal runtime disposal always awaits actual drain.
            try { if (counter is not null) counter.DisposeAsync().GetAwaiter().GetResult(); } catch { cleanupFailed = true; }
            throw new InvalidOperationException(cleanupFailed
                ? "AI runtime initialization and partial resource cleanup failed."
                : "AI runtime initialization failed.");
        }
    }
}
