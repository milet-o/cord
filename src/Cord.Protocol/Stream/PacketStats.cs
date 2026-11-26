using System.Diagnostics;

namespace Cord.Protocol.Stream;

/// <summary>Counts received and lost packets from 16-bit sequence numbers and tracks jitter and loss %.</summary>
public sealed class PacketStats
{
    private readonly Lock _lock = new();
    private int? _last;
    private int _received;
    private int _lost;
    private long _lastArrivalTicks;
    private double _lastDeltaMs;
    private double _jitterMs;
    private double _lastLossPercent;

    public double JitterMs
    {
        get
        {
            lock (_lock)
                return _jitterMs;
        }
    }

    public double LastLossPercent
    {
        get
        {
            lock (_lock)
                return _lastLossPercent;
        }
    }

    public void Record(ushort sequence)
    {
        var nowTicks = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            if (_lastArrivalTicks != 0)
            {
                var deltaMs = (nowTicks - _lastArrivalTicks) * 1000.0 / Stopwatch.Frequency;
                if (_lastDeltaMs > 0)
                {
                    var d = Math.Abs(deltaMs - _lastDeltaMs);
                    _jitterMs += (d - _jitterMs) / 16.0;
                }

                _lastDeltaMs = deltaMs;
            }

            _lastArrivalTicks = nowTicks;
            _received++;
            if (_last is { } last)
            {
                var gap = unchecked((short)(sequence - (ushort)last)) - 1;
                if (gap < 0)
                    return;
                _lost += gap;
            }

            _last = sequence;
        }
    }

    /// <summary>Returns the counts since the previous call.</summary>
    public (int Received, int Lost) TakeSnapshot()
    {
        lock (_lock)
        {
            var snapshot = (_received, _lost);
            var total = _received + _lost;
            _lastLossPercent = total > 0 ? (_lost * 100.0 / total) : 0;
            (_received, _lost) = (0, 0);
            return snapshot;
        }
    }

    /// <summary>Caps the loss ratio as described in docs/protocol/14-congestion.md.</summary>
    public static (ushort Received, ushort Lost) Cap(int received, int lost, double maxLoss)
    {
        var total = received + lost;
        if (total > 0 && (double)lost / total > maxLoss)
        {
            lost = (int)(total * maxLoss);
            received = total - lost;
        }

        return ((ushort)Math.Min(received, ushort.MaxValue), (ushort)Math.Min(lost, ushort.MaxValue));
    }

    /// <summary>
    /// Merges real LAN packet counts with an intentional synthetic loss ratio (0..0.30) used by
    /// adaptive bitrate mode. Real loss is capped by <paramref name="maxLoss"/>, while intentional
    /// synthetic loss overrides that cap when higher so the console scales bitrate down smoothly.
    /// </summary>
    public static (ushort Received, ushort Lost) CombineWithSyntheticLoss(
        int received,
        int lost,
        double maxLoss,
        double syntheticLossRatio)
    {
        var capped = Cap(received, lost, maxLoss);
        var synthetic = Math.Clamp(syntheticLossRatio, 0.0, 0.30);
        if (synthetic <= 0.0001)
            return capped;

        var total = capped.Received + capped.Lost;
        if (total == 0)
            return (0, 0);

        var syntheticLost = Math.Clamp((int)Math.Ceiling(total * synthetic), 1, total);
        if (syntheticLost <= capped.Lost)
            return capped;

        return ((ushort)(total - syntheticLost), (ushort)syntheticLost);
    }
}
