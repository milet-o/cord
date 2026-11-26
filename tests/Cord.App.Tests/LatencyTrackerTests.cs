using System.Diagnostics;
using Cord.App.Streaming;

namespace Cord.App.Tests;

public sealed class LatencyTrackerTests
{
    [Fact]
    public void ComputesAverageAndP95AcrossRecordedFrames()
    {
        var tracker = new LatencyTracker();
        var ticksPerMs = Stopwatch.Frequency / 1000.0;

        for (var i = 1; i <= 20; i++)
        {
            var t0 = 1000L * (long)ticksPerMs;
            var t1 = t0 + (long)(1.0 * ticksPerMs);          // 1 ms queue
            var t2 = t1 + (long)(2.0 * ticksPerMs);          // 2 ms decode
            var t3 = t2 + (long)((i >= 19 ? 10.0 : 1.0) * ticksPerMs); // 18x 1 ms present, 2x 10 ms spikes
            tracker.RecordFrame(t0, t1, t2, t3);
        }

        var snap = tracker.Snapshot();
        Assert.InRange(snap.AvgQueueMs, 0.9, 1.1);
        Assert.InRange(snap.AvgDecodeMs, 1.9, 2.1);
        Assert.InRange(snap.AvgPresentMs, 1.8, 2.0);
        Assert.InRange(snap.AvgTotalMs, 4.8, 5.0);
        Assert.InRange(snap.P95TotalMs, 12.5, 13.5);
    }

    [Fact]
    public void IgnoresInvalidNonMonotonicTimestamps()
    {
        var tracker = new LatencyTracker();
        tracker.RecordFrame(100, 90, 110, 120);

        var snap = tracker.Snapshot();
        Assert.Equal(0, snap.AvgTotalMs);
    }
}
