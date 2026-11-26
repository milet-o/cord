namespace Cord.Core.Consoles;

/// <summary>
/// Thread-safe AIMD-style controller that maps web tunnel queue backpressure and browser RTT spikes
/// into a synthetic congestion loss ratio (0..0.22) reported to the console so it automatically
/// scales stream bitrate down on congestion and climbs back up when smooth.
/// </summary>
public sealed class AdaptiveBitrateController
{
    public const double MaxLossRatio = 0.10;

    private readonly Lock _lock = new();
    private double _currentLossRatio;
    private double _baselineRttMs;
    private long _lastCongestionMs;
    private long _lastDecayMs;

    public double CurrentLossRatio
    {
        get
        {
            lock (_lock)
                return _currentLossRatio;
        }
    }

    public int PressurePercent
    {
        get
        {
            var loss = CurrentLossRatio;
            return (int)Math.Round(Math.Clamp(loss / MaxLossRatio, 0.0, 1.0) * 100.0);
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _currentLossRatio = 0;
            _baselineRttMs = 0;
            _lastCongestionMs = 0;
            _lastDecayMs = 0;
        }
    }

    public double Observe(long nowMs, bool queueOverflow, int queueDepth, int rttMs, int decodeQueueDepth)
    {
        lock (_lock)
        {
            if (rttMs > 0)
            {
                if (_baselineRttMs <= 0)
                    _baselineRttMs = rttMs;
                else if (rttMs < _baselineRttMs)
                    _baselineRttMs = (_baselineRttMs * 0.75) + (rttMs * 0.25);
            }

            var rttSpiked = rttMs > 0
                && _baselineRttMs > 0
                && rttMs > _baselineRttMs + Math.Max(45.0, _baselineRttMs * 0.65);

            if (queueOverflow || decodeQueueDepth >= 6)
            {
                _currentLossRatio = Math.Clamp(Math.Max(_currentLossRatio + 0.04, 0.05), 0.0, MaxLossRatio);
                _lastCongestionMs = nowMs;
            }
            else if (queueDepth >= 4 || decodeQueueDepth >= 4 || rttSpiked)
            {
                _currentLossRatio = Math.Clamp(Math.Max(_currentLossRatio + 0.02, 0.025), 0.0, MaxLossRatio);
                _lastCongestionMs = nowMs;
            }
            else
            {
                if (rttMs > 0 && _baselineRttMs > 0)
                    _baselineRttMs = (_baselineRttMs * 0.96) + (rttMs * 0.04);

                if (_currentLossRatio > 0 && nowMs - _lastCongestionMs >= 1500 && nowMs - _lastDecayMs >= 400)
                {
                    _currentLossRatio = Math.Max(0.0, _currentLossRatio - 0.025);
                    _lastDecayMs = nowMs;
                }
            }

            return _currentLossRatio;
        }
    }
}
