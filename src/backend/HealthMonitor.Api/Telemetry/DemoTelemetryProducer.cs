using HealthMonitor.Api.Provisioning;
using HealthMonitor.Application.Device;
using HealthMonitor.Application.Telemetry;

namespace HealthMonitor.Api.Telemetry;

/// <summary>Produces bounded synthetic observations through the existing Application transaction boundary.</summary>
/// <remarks>
/// One enabled API replica is required. Fixed device membership never provisions inventory or includes unrelated devices.
/// Each group retains an uncertain request unchanged for receipt-first replay. Expiry or explicit ineligibility skips
/// that opportunity, not proof that an earlier attempt rolled back. A later scheduled observation is new work.
/// Pending identities and rotation are process-local: restart does not provide durable recovery or exactly-once delivery.
/// </remarks>
internal sealed class DemoTelemetryProducer : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly string[] Scenarios = ["Healthy", "HighLatency", "HighCpu", "Timeout"];
    private static readonly Guid[][] Groups = Enumerable.Range(0, 4)
        .Select(group => FixedDeviceCatalog.Devices.Skip(group * 6).Take(6).Select(device => device.Id).ToArray())
        .ToArray();
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<DemoTelemetryProducer> _logger;
    private readonly PendingGroup[] _pending = Enumerable.Range(0, 4).Select(_ => new PendingGroup()).ToArray();

    /// <summary>Creates a singleton worker without capturing any scoped database service.</summary>
    /// <param name="scopes">Creates independently disposed catalog and write-attempt scopes.</param>
    /// <param name="time">Monotonic scheduling clock and UTC source for UUIDv7 creation.</param>
    /// <param name="logger">Receives sanitized operational outcomes, never provider exception details.</param>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    public DemoTelemetryProducer(IServiceScopeFactory scopes, TimeProvider time, ILogger<DemoTelemetryProducer> logger)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Waits for scheduled minute boundaries and processes groups sequentially without catch-up bursts.</summary>
    /// <param name="stoppingToken">Host shutdown cancellation, propagated to delays, catalog reads, and ingestion.</param>
    /// <returns>Completion when shutdown is requested.</returns>
    /// <remarks>
    /// The first cycle is delayed one minute. Monotonic elapsed time prevents wall-clock changes from shifting cadence.
    /// Slow cycles skip elapsed boundaries; assignment holds for four scheduled cycles, then rotates, repeating after 16.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long start = _time.GetTimestamp();
        long nextTick = 1;
        try
        {
            while (true)
            {
                var delay = TimeSpan.FromTicks(nextTick * Interval.Ticks) - _time.GetElapsedTime(start);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, _time, stoppingToken);
                stoppingToken.ThrowIfCancellationRequested();
                long tick = Math.Max(nextTick, _time.GetElapsedTime(start).Ticks / Interval.Ticks);
                for (int group = 0; group < Groups.Length; group++)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    await ObserveGroupAsync(group, tick - 1, stoppingToken);
                }

                // Select a future boundary, rather than retaining a missed tick or drifting by processing duration.
                nextTick = Math.Max(tick + 1, _time.GetElapsedTime(start).Ticks / Interval.Ticks + 1);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown can leave a commit uncertain. Do not claim rollback or invent a replacement request here.
        }
    }

    /// <summary>Replays pending work before inspecting current eligibility, or creates one new group observation.</summary>
    /// <param name="group">Stable six-device group index.</param>
    /// <param name="cycle">Zero-based scheduled cycle, including skipped boundaries.</param>
    /// <param name="token">Host shutdown cancellation.</param>
    /// <returns>Completion of this group's single attempt; other groups remain independent on operational failure.</returns>
    private async Task ObserveGroupAsync(int group, long cycle, CancellationToken token)
    {
        var pending = _pending[group];
        if (pending.Suspended)
            return;
        try
        {
            if (pending.Request is null)
            {
                // A retired registration must not suppress receipt replay. Filter only newly constructed observations.
                await using var catalogScope = _scopes.CreateAsyncScope();
                var catalog = catalogScope.ServiceProvider.GetRequiredService<IDeviceCatalogQueries>();
                var registrations = await catalog.GetByIdsAsync(Groups[group], token);
                var active = registrations.Where(device => !device.IsRetired).Select(device => device.DeviceId).ToHashSet();
                var ids = Groups[group].Where(active.Contains).ToArray();
                if (ids.Length == 0)
                    return;
                string scenario = Scenarios[(group + (int)(cycle % 16 / 4)) % Scenarios.Length];
                pending.Request = new(Guid.CreateVersion7(_time.GetUtcNow()), scenario, Array.AsReadOnly(ids));
            }

            // A failed transaction may dispose its context; every attempt therefore owns a fresh scope.
            await using var writeScope = _scopes.CreateAsyncScope();
            var ingestion = writeScope.ServiceProvider.GetRequiredService<ITelemetryIngestionService>();
            await ingestion.IngestAsync(pending.Request, token);
            pending.Request = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (TelemetryBatchIdValidationException error) when (error.Code == "batch_id_expired")
        {
            SkipOpportunity(group, pending, "expired");
        }
        catch (TelemetryDeviceEligibilityException)
        {
            SkipOpportunity(group, pending, "ineligible");
        }
        catch (Exception error) when (error is TelemetryIngestionConflictException or TelemetryBatchIdValidationException)
        {
            // A conflict or malformed/future generated ID is an invariant failure, not an outage or expiry.
            pending.Suspended = true;
            _logger.LogError("Synthetic group {Group} suspended; reconcile batch {BatchId} and restart the producer.",
                group, pending.Request?.BatchId);
        }
        catch (Exception)
        {
            // No exception object/message is logged: provider failures can contain connection or SQL details.
            // Unknown commit outcomes retain the exact identity and payload; the next minute is the bounded backoff.
            _logger.LogWarning("Synthetic group {Group} unavailable; retry pending batch {BatchId} next cycle.",
                group, pending.Request?.BatchId);
        }
    }

    /// <summary>Abandons only this expired/ineligible opportunity, allowing fresh work at the next scheduled boundary.</summary>
    /// <param name="group">Affected fixed group.</param>
    /// <param name="pending">Retained original identity, cleared only after its sanitized diagnostic is emitted.</param>
    /// <param name="reason">Producer-owned constant; never provider exception text.</param>
    /// <remarks>
    /// Receipt-first rejection proves only that this attempt admitted no writes. An earlier uncertain commit remains
    /// possible. No replacement ID is generated in this cycle; subsequent observations use fresh time and rotation.
    /// </remarks>
    private void SkipOpportunity(int group, PendingGroup pending, string reason)
    {
        _logger.LogWarning("Synthetic observation skipped for group {Group}, batch {BatchId}, reason {Reason}; prior commit may exist.",
            group, pending.Request?.BatchId, reason);
        pending.Request = null;
    }

    /// <summary>Bounds process-local recovery to one immutable request per group; invariant failures require operator recovery.</summary>
    private sealed class PendingGroup
    {
        /// <summary>Original request retained across uncertain outcomes; never rewritten with a replacement identity.</summary>
        public TelemetryIngestionRequest? Request { get; set; }

        /// <summary>Whether an invariant failure blocks this group until reconciliation and process restart.</summary>
        public bool Suspended { get; set; }
    }
}
