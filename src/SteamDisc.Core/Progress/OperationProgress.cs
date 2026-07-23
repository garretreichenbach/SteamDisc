using System.Globalization;

namespace SteamDisc.Core.Progress;

public enum OperationPhase
{
    Preparing,
    Scanning,
    Hashing,
    Compressing,
    Extracting,
    Verifying,
    WritingManifest,
    RunningPrerequisites,
    BuildingImage,
    Burning,
    Finishing,
}

/// <summary>A progress snapshot. Immutable, so it is safe to hand across threads.</summary>
public readonly record struct OperationProgress(
    OperationPhase Phase,
    long BytesCompleted,
    long BytesTotal,
    int ItemsCompleted,
    int ItemsTotal,
    string? CurrentItem,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining)
{
    /// <summary>Fraction complete in 0..1, or null when the total is not known.</summary>
    public double? Fraction => BytesTotal > 0
        ? Math.Clamp((double)BytesCompleted / BytesTotal, 0d, 1d)
        : null;

    public double? BytesPerSecond => Elapsed > TimeSpan.Zero
        ? BytesCompleted / Elapsed.TotalSeconds
        : null;

    public override string ToString()
    {
        var percent = Fraction is { } f
            ? (f * 100).ToString("F1", CultureInfo.InvariantCulture) + "%"
            : "?";
        return $"{Phase} {percent} {CurrentItem}";
    }
}

/// <summary>
/// Turns raw byte counts into progress reports, including a time estimate.
/// </summary>
/// <remarks>
/// The estimate uses a decaying average of recent throughput rather than the overall mean.
/// Optical reads are not uniform — a BD-R drive spins up, seeks, and slows toward the outer
/// edge — and an estimate anchored to the first ten seconds of a 25-minute read is a lie.
/// No estimate is offered until enough data has moved for one to mean anything.
/// </remarks>
public sealed class ProgressTracker
{
    private const double SmoothingFactor = 0.15;
    private const long MinimumBytesBeforeEstimating = 8L * 1024 * 1024;
    private static readonly TimeSpan MinimumTimeBeforeEstimating = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);

    private readonly IProgress<OperationProgress>? _sink;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly object _gate = new();

    private double _smoothedBytesPerSecond;
    private long _lastSampleBytes;
    private TimeSpan _lastSampleTime;
    private TimeSpan _lastReport = TimeSpan.MinValue;

    private OperationPhase _phase;
    private long _bytesCompleted;
    private long _bytesTotal;
    private int _itemsCompleted;
    private int _itemsTotal;
    private string? _currentItem;

    public ProgressTracker(IProgress<OperationProgress>? sink, OperationPhase initialPhase = OperationPhase.Preparing)
    {
        _sink = sink;
        _phase = initialPhase;
    }

    public long BytesCompleted
    {
        get
        {
            lock (_gate)
            {
                return _bytesCompleted;
            }
        }
    }

    public void SetTotals(long bytesTotal, int itemsTotal = 0)
    {
        lock (_gate)
        {
            _bytesTotal = bytesTotal;
            _itemsTotal = itemsTotal;
        }

        Report(force: true);
    }

    public void SetPhase(OperationPhase phase, string? currentItem = null)
    {
        lock (_gate)
        {
            _phase = phase;
            _currentItem = currentItem;
        }

        Report(force: true);
    }

    public void SetCurrentItem(string? currentItem)
    {
        lock (_gate)
        {
            _currentItem = currentItem;
        }

        Report(force: false);
    }

    public void AddBytes(long bytes)
    {
        lock (_gate)
        {
            _bytesCompleted += bytes;
        }

        Report(force: false);
    }

    public void CompleteItem(long bytes = 0)
    {
        lock (_gate)
        {
            _itemsCompleted++;
            _bytesCompleted += bytes;
        }

        Report(force: false);
    }

    /// <summary>Emits a final 100% report so a UI never freezes at 99%.</summary>
    public void Finish()
    {
        lock (_gate)
        {
            if (_bytesTotal > 0)
            {
                _bytesCompleted = _bytesTotal;
            }

            if (_itemsTotal > 0)
            {
                _itemsCompleted = _itemsTotal;
            }
        }

        Report(force: true);
    }

    public OperationProgress Snapshot()
    {
        lock (_gate)
        {
            return BuildSnapshotLocked();
        }
    }

    private void Report(bool force)
    {
        if (_sink is null)
        {
            return;
        }

        OperationProgress snapshot;
        lock (_gate)
        {
            var now = _clock.Elapsed;
            if (!force && now - _lastReport < ReportInterval)
            {
                return;
            }

            _lastReport = now;
            snapshot = BuildSnapshotLocked();
        }

        _sink.Report(snapshot);
    }

    private OperationProgress BuildSnapshotLocked()
    {
        var elapsed = _clock.Elapsed;
        UpdateRateLocked(elapsed);

        TimeSpan? remaining = null;
        if (_bytesTotal > 0 &&
            _bytesCompleted >= MinimumBytesBeforeEstimating &&
            elapsed >= MinimumTimeBeforeEstimating &&
            _smoothedBytesPerSecond > 1)
        {
            var seconds = (_bytesTotal - _bytesCompleted) / _smoothedBytesPerSecond;
            if (seconds is >= 0 and < 60 * 60 * 24)
            {
                remaining = TimeSpan.FromSeconds(seconds);
            }
        }

        return new OperationProgress(
            _phase,
            _bytesCompleted,
            _bytesTotal,
            _itemsCompleted,
            _itemsTotal,
            _currentItem,
            elapsed,
            remaining);
    }

    private void UpdateRateLocked(TimeSpan now)
    {
        var window = now - _lastSampleTime;
        if (window < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        var delta = _bytesCompleted - _lastSampleBytes;
        var instantaneous = delta / window.TotalSeconds;

        _smoothedBytesPerSecond = _smoothedBytesPerSecond <= 0
            ? instantaneous
            : (SmoothingFactor * instantaneous) + ((1 - SmoothingFactor) * _smoothedBytesPerSecond);

        _lastSampleBytes = _bytesCompleted;
        _lastSampleTime = now;
    }
}
