using HealthMonitor.Application.Telemetry;

namespace HealthMonitor.Api.Telemetry;

/// <summary>Runs bounded retention at startup and hourly, independently of synthetic producer enablement.</summary>
/// <remarks>
/// Every attempt owns a fresh scope. Successful attempts return to monotonic startup-anchored hourly boundaries;
/// slow passes skip missed boundaries. Failures back off from one minute to one hour. The delay resets only after
/// both the pass and scope cleanup succeed. No startup migration or inventory provisioning is performed.
/// </remarks>
internal sealed class TelemetryRetentionWorker : BackgroundService
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<TelemetryRetentionWorker> _logger;

    /// <summary>Creates a worker without resolving any scoped context or opening a connection.</summary>
    /// <param name="scopes">Owns fresh Application/store scopes for each bounded pass.</param>
    /// <param name="time">Monotonic scheduling clock; the Application service separately captures UTC cutoffs.</param>
    /// <param name="logger">Receives only worker-owned diagnostics and confirmed counts.</param>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    public TelemetryRetentionWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<TelemetryRetentionWorker> logger)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Starts immediately, then serializes hourly passes or bounded failure retries until shutdown.</summary>
    /// <param name="stoppingToken">Host shutdown propagated through delays and Application/store work.</param>
    /// <returns>Completion after in-flight work and scope cleanup terminate.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long start = _time.GetTimestamp();
        int backoffSeconds = 60;
        TimeSpan delay = TimeSpan.Zero;
        try
        {
            while (true)
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, _time, stoppingToken);
                stoppingToken.ThrowIfCancellationRequested();
                bool succeeded = await RunAttemptAsync(stoppingToken);
                if (succeeded)
                {
                    backoffSeconds = 60;
                    var elapsed = _time.GetElapsedTime(start);
                    // Always choose a future hourly boundary; never queue missed hourly ticks behind a slow pass.
                    delay = TimeSpan.FromTicks((elapsed.Ticks / Hour.Ticks + 1) * Hour.Ticks) - elapsed;
                }
                else
                {
                    delay = TimeSpan.FromSeconds(backoffSeconds);
                    backoffSeconds = Math.Min(backoffSeconds * 2, 3600);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Cancellation is not rollback evidence; the adapter and confirmed-progress snapshot own that distinction.
        }
    }

    /// <summary>Captures confirmed progress before disposing its scope, distinguishing work failure from cleanup failure.</summary>
    /// <param name="token">Shutdown cancellation; cleanup still runs after the pass terminates.</param>
    /// <returns>True only when the pass and scope disposal both succeeded.</returns>
    private async Task<bool> RunAttemptAsync(CancellationToken token)
    {
        AsyncServiceScope? scope = null;
        TelemetryRetentionService? service = null;
        TelemetryRetentionPassResult? confirmed = null;
        bool passFailed = false;
        bool cleanupFailed = false;
        try
        {
            scope = _scopes.CreateAsyncScope();
            service = scope.Value.ServiceProvider.GetRequiredService<TelemetryRetentionService>();
            await service.RunPassAsync(token);
        }
        catch (Exception) { passFailed = true; }
        finally
        {
            // Failure may follow earlier committed batches, or an uncertain current commit. Preserve only confirmed returns.
            confirmed = service?.LastConfirmedProgress;
            if (scope is { } owned)
            {
                try { await owned.DisposeAsync(); }
                catch (Exception) { cleanupFailed = true; }
            }
        }

        Report(confirmed, passFailed, cleanupFailed);
        token.ThrowIfCancellationRequested();
        return !passFailed && !cleanupFailed;
    }

    /// <summary>Reports bounded results without provider exception objects, SQL, or connection diagnostics.</summary>
    /// <param name="confirmed">Last confirmed aggregate captured before scope disposal; null means none acknowledged.</param>
    /// <param name="passFailed">Whether the pass failed; an unconfirmed batch may still have committed.</param>
    /// <param name="cleanupFailed">Whether scope cleanup failed, which requires a fresh-scope retry even after a successful pass.</param>
    private void Report(TelemetryRetentionPassResult? confirmed, bool passFailed, bool cleanupFailed)
    {
        int batches = confirmed?.CompletedBatches ?? 0;
        int rows = (confirmed?.DeletedPings ?? 0) + (confirmed?.DeletedIncidents ?? 0) + (confirmed?.DeletedReceipts ?? 0);
        try
        {
            if (passFailed)
                _logger.LogWarning("Retention pass failed; confirmed batches {Batches}, confirmed deletions {Rows}; unconfirmed commit may exist.", batches, rows);
            if (cleanupFailed)
                _logger.LogWarning("Retention scope cleanup failed; confirmed batches {Batches}, confirmed deletions {Rows}; next attempt uses a fresh scope.", batches, rows);
            if (!passFailed && !cleanupFailed)
            {
                _logger.LogInformation("Retention pass completed; confirmed batches {Batches}, confirmed deletions {Rows}.", batches, rows);
                if (confirmed?.HasBacklog == true)
                    _logger.LogWarning("Retention eligible backlog remains at the last locked observation; next pass remains hourly.");
            }
        }
        catch { /* Diagnostic sinks must not alter confirmed outcomes or prevent scope-safe recovery. */ }
    }
}
