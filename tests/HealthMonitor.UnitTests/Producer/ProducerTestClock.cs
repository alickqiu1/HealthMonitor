using System.Threading.Channels;

namespace HealthMonitor.UnitTests.Producer;

/// <summary>Explicitly advanced monotonic/UTC clock for producer delay tests, requiring no real minute-long sleeps.</summary>
/// <remarks>
/// Timer notifications let tests wait until the worker has scheduled its next delay before advancing time.
/// Callbacks execute outside the clock lock, matching the reentrant disposal required by Task.Delay cancellation.
/// The producer uses one-shot timers; periodic behavior is supported for completeness without background threads.
/// </remarks>
internal sealed class ProducerTestClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly Channel<ManualTimer> _scheduled = Channel.CreateUnbounded<ManualTimer>();
    private long _ticks;
    private DateTimeOffset _utc = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() { lock (_gate) return _utc; }
    /// <inheritdoc />
    public override long GetTimestamp() { lock (_gate) return _ticks; }

    /// <summary>Schedules a controlled timer and signals test synchronization only for a finite due time.</summary>
    /// <param name="callback">Callback invoked when explicit advancement reaches the deadline.</param>
    /// <param name="state">Callback state.</param>
    /// <param name="dueTime">Relative deadline or infinite.</param>
    /// <param name="period">Repeat interval or infinite.</param>
    /// <returns>A timer that can be safely disposed by a callback or cancellation.</returns>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Waits for a worker delay to be registered; the outer deadline makes stalled workers fail promptly.</summary>
    /// <returns>The remaining duration until the scheduled deadline.</returns>
    public async Task<TimeSpan> NextDelayAsync()
    {
        var timer = await _scheduled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        lock (_gate) return TimeSpan.FromTicks(timer.Due - _ticks);
    }

    /// <summary>Advances both clocks and fires due timers without real waiting.</summary>
    /// <param name="duration">Nonnegative elapsed time.</param>
    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        List<ManualTimer> due;
        lock (_gate)
        {
            _ticks += duration.Ticks;
            _utc += duration;
            due = _timers.Where(timer => timer.Due <= _ticks).ToList();
            foreach (var timer in due)
            {
                _timers.Remove(timer);
                if (timer.Period > 0)
                {
                    timer.Due += ((_ticks - timer.Due) / timer.Period + 1) * timer.Period;
                    _timers.Add(timer);
                }
            }
        }
        foreach (var timer in due) timer.Fire();
    }

    /// <summary>Moves UTC independently to test that scheduling does not depend on wall-clock continuity.</summary>
    /// <param name="offset">Wall-clock correction; monotonic time is unchanged.</param>
    public void AdjustUtc(TimeSpan offset) { lock (_gate) _utc += offset; }

    /// <summary>Owned timer whose lifecycle is synchronized with the clock, including callback-time disposal.</summary>
    private sealed class ManualTimer(ProducerTestClock owner, TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;
        /// <summary>Absolute monotonic deadline in clock ticks.</summary>
        internal long Due { get; set; }
        /// <summary>Repeat interval, or a nonpositive value for one-shot/infinite behavior.</summary>
        internal long Period { get; set; }
        /// <summary>Invokes the callback only while the timer remains live.</summary>
        internal void Fire()
        {
            lock (owner._gate) { if (_disposed) return; }
            callback(state);
        }
        /// <inheritdoc />
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (_disposed) return false;
                owner._timers.Remove(this);
                Due = owner._ticks + dueTime.Ticks;
                Period = period.Ticks;
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    owner._timers.Add(this);
                    owner._scheduled.Writer.TryWrite(this);
                }
                return true;
            }
        }
        /// <inheritdoc />
        public void Dispose()
        {
            lock (owner._gate) { _disposed = true; owner._timers.Remove(this); }
        }
        /// <inheritdoc />
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
