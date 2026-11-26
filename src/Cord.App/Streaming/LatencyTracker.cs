using System.Diagnostics;

namespace Cord.App.Streaming;

internal sealed record LatencySnapshot(
    double AvgQueueMs,
    double AvgDecodeMs,
    double AvgPresentMs,
    double AvgTotalMs,
    double P95TotalMs,
    double InputPollHz);

/// <summary>
/// Records per-frame client pipeline latencies (queue wait, decode time, present wait, total) and
/// actual input polling frequency using a fixed rolling ring buffer.
/// </summary>
internal sealed class LatencyTracker
{
    private const int Capacity = 120;

    private readonly Lock _lock = new();
    private readonly double[] _queueMs = new double[Capacity];
    private readonly double[] _decodeMs = new double[Capacity];
    private readonly double[] _presentMs = new double[Capacity];
    private readonly double[] _totalMs = new double[Capacity];
    private readonly double[] _sortScratch = new double[Capacity];
    private int _count;
    private int _writeIndex;

    private long _inputWindowStart = Stopwatch.GetTimestamp();
    private int _inputPollsInWindow;
    private double _lastInputPollHz;

    public void RecordFrame(long enqueuedTimestamp, long decodeStartTimestamp, long decodedTimestamp, long presentedTimestamp)
    {
        if (enqueuedTimestamp <= 0 || decodeStartTimestamp < enqueuedTimestamp || decodedTimestamp < decodeStartTimestamp || presentedTimestamp < decodedTimestamp)
            return;

        var queue = ToMs(decodeStartTimestamp - enqueuedTimestamp);
        var decode = ToMs(decodedTimestamp - decodeStartTimestamp);
        var present = ToMs(presentedTimestamp - decodedTimestamp);
        var total = ToMs(presentedTimestamp - enqueuedTimestamp);

        lock (_lock)
        {
            _queueMs[_writeIndex] = queue;
            _decodeMs[_writeIndex] = decode;
            _presentMs[_writeIndex] = present;
            _totalMs[_writeIndex] = total;
            _writeIndex = (_writeIndex + 1) % Capacity;
            if (_count < Capacity)
                _count++;
        }
    }

    public void RecordInputPoll()
    {
        var now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            _inputPollsInWindow++;
            var elapsedSec = (double)(now - _inputWindowStart) / Stopwatch.Frequency;
            if (elapsedSec >= 0.5)
            {
                _lastInputPollHz = _inputPollsInWindow / elapsedSec;
                _inputPollsInWindow = 0;
                _inputWindowStart = now;
            }
        }
    }

    public LatencySnapshot Snapshot()
    {
        lock (_lock)
        {
            if (_count == 0)
                return new LatencySnapshot(0, 0, 0, 0, 0, _lastInputPollHz);

            double sumQueue = 0;
            double sumDecode = 0;
            double sumPresent = 0;
            double sumTotal = 0;
            for (var i = 0; i < _count; i++)
            {
                sumQueue += _queueMs[i];
                sumDecode += _decodeMs[i];
                sumPresent += _presentMs[i];
                sumTotal += _totalMs[i];
                _sortScratch[i] = _totalMs[i];
            }

            Array.Sort(_sortScratch, 0, _count);
            var p95Index = Math.Clamp((int)Math.Ceiling(_count * 0.95) - 1, 0, _count - 1);
            return new LatencySnapshot(
                sumQueue / _count,
                sumDecode / _count,
                sumPresent / _count,
                sumTotal / _count,
                _sortScratch[p95Index],
                _lastInputPollHz);
        }
    }

    private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
