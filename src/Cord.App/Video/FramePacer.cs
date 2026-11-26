using Cord.Core.Settings;

namespace Cord.App.Video;

internal readonly record struct FramePacingDecision(
    bool PresentPreviousFrame,
    bool UseFrameGen,
    float Alpha,
    bool ComputeOpticalFlow);

/// <summary>
/// Tracks unique vs duplicate video cadence (distinguishing alternating 30 fps cadence from
/// static scenes where the player stands still), detects scene cuts from GPU MAD metrics,
/// and computes bounded interpolation/extrapolation factors.
/// </summary>
internal sealed class FramePacer
{
    // Normalized luma MAD thresholds in [0..1].
    internal const float DuplicateMadThreshold = 0.0022f;
    internal const float SceneCutMadThreshold = 0.22f;
    private const double MinDisplayHzFor60To120 = 100.0;

    private readonly bool[] _alternatingDupWindow = new bool[16];
    private int _windowCount;
    private int _windowIndex;
    private bool _prevWasDuplicate;
    private int _duplicateStreak;
    private double _lastUniqueTimestampMs = -1;
    private double _smoothedUniqueIntervalMs = 16.67;
    private bool _hasTwoUniqueFrames;
    private bool _sceneCutActive;
    private float _lastPresentedAlpha;
    private long _generatedFrames;

    public int DetectedSourceFps { get; private set; } = 60;

    public long GeneratedFrames => _generatedFrames;

    public bool HasTwoUniqueFrames => _hasTwoUniqueFrames;

    /// <summary>True when 2+ consecutive frames are near-identical (player/camera standing still).</summary>
    public bool IsStaticScene => _duplicateStreak >= 2;

    public string StatusLabel(FrameGenMode mode, float displayRefreshHz)
    {
        if (mode == FrameGenMode.Off)
            return "Off";

        if (IsStaticScene)
            return "Static (passthrough)";

        var targetFps = DetectedSourceFps <= 30 ? 60 : (displayRefreshHz >= MinDisplayHzFor60To120 ? (int)MathF.Round(displayRefreshHz) : 60);
        if (DetectedSourceFps >= 60 && displayRefreshHz < MinDisplayHzFor60To120)
            return "Passthrough (60 Hz)";

        var kind = mode == FrameGenMode.LowLatencyExtrapolation ? "Extrapolation" : "Interpolation";
        return $"{kind} {DetectedSourceFps}->{targetFps}";
    }

    public void Reset()
    {
        Array.Clear(_alternatingDupWindow);
        _windowCount = 0;
        _windowIndex = 0;
        _prevWasDuplicate = false;
        _duplicateStreak = 0;
        _lastUniqueTimestampMs = -1;
        _smoothedUniqueIntervalMs = 16.67;
        _hasTwoUniqueFrames = false;
        _sceneCutActive = false;
        _lastPresentedAlpha = 0f;
        DetectedSourceFps = 60;
        _generatedFrames = 0;
    }

    /// <summary>
    /// Called whenever a newly decoded video frame arrives from the console.
    /// Returns true if the frame is unique and Optical Flow should run.
    /// </summary>
    public bool OnNewSourceFrame(float mad, double nowMs, FrameGenMode mode, out bool sceneCut)
    {
        if (!_hasTwoUniqueFrames && _lastUniqueTimestampMs < 0)
        {
            _lastUniqueTimestampMs = nowMs;
            sceneCut = false;
            return false;
        }

        sceneCut = mad >= SceneCutMadThreshold;
        _sceneCutActive = sceneCut;
        var isDuplicate = !sceneCut && mad <= DuplicateMadThreshold;

        // Only isolated alternating duplicates (unique -> dup -> unique) indicate a 30 fps game;
        // consecutive duplicates mean a static scene and must NOT trigger 30->60 Frame Gen.
        var isAlternatingDup = isDuplicate && !_prevWasDuplicate;
        _alternatingDupWindow[_windowIndex] = isAlternatingDup;
        _windowIndex = (_windowIndex + 1) % _alternatingDupWindow.Length;
        if (_windowCount < _alternatingDupWindow.Length)
            _windowCount++;

        var altDups = 0;
        for (var i = 0; i < _windowCount; i++)
        {
            if (_alternatingDupWindow[i])
                altDups++;
        }

        DetectedSourceFps = (_windowCount >= 6 && altDups >= 3) ? 30 : 60;
        _prevWasDuplicate = isDuplicate;

        if (isDuplicate)
        {
            _duplicateStreak++;
            return false;
        }

        _duplicateStreak = 0;
        if (_lastUniqueTimestampMs >= 0)
        {
            var delta = nowMs - _lastUniqueTimestampMs;
            if (delta is >= 6.0 and <= 100.0)
                _smoothedUniqueIntervalMs = _smoothedUniqueIntervalMs * 0.8 + delta * 0.2;
        }

        _lastUniqueTimestampMs = nowMs;
        _hasTwoUniqueFrames = true;
        _lastPresentedAlpha = 0f;
        return mode != FrameGenMode.Off && !sceneCut;
    }

    /// <summary>Chooses whether the current render tick presents a real frame or a generated one.</summary>
    public FramePacingDecision EvaluateRenderTick(
        FrameGenMode mode,
        float displayRefreshHz,
        double nowMs,
        bool isFreshUniqueFrameThisTick)
    {
        if (mode == FrameGenMode.Off || !_hasTwoUniqueFrames || _sceneCutActive || IsStaticScene)
            return new FramePacingDecision(false, false, 0f, false);

        var needsGeneration = DetectedSourceFps <= 30 || displayRefreshHz >= MinDisplayHzFor60To120;
        if (!needsGeneration)
            return new FramePacingDecision(false, false, 0f, false);

        if (mode == FrameGenMode.LowLatencyExtrapolation)
        {
            // Zero extra queue: fresh unique frames always present immediately (alpha = 0).
            if (isFreshUniqueFrameThisTick)
            {
                _lastPresentedAlpha = 0f;
                return new FramePacingDecision(false, false, 0f, false);
            }

            var elapsed = Math.Max(0.0, nowMs - _lastUniqueTimestampMs);
            var rawAlpha = DetectedSourceFps <= 30 && _duplicateStreak == 1
                ? 0.5f
                : Math.Clamp((float)(elapsed / Math.Max(8.0, _smoothedUniqueIntervalMs)), 0.10f, 0.52f);
            var alpha = Math.Max(_lastPresentedAlpha, rawAlpha);
            _lastPresentedAlpha = alpha;
            _generatedFrames++;
            return new FramePacingDecision(false, true, alpha, false);
        }

        // SmoothInterpolation: holds 1 frame back on alternating motion, interpolates Prev->Curr monotonically.
        if (isFreshUniqueFrameThisTick)
        {
            _lastPresentedAlpha = 0f;
            return new FramePacingDecision(PresentPreviousFrame: true, UseFrameGen: false, Alpha: 0f, ComputeOpticalFlow: false);
        }

        var interpElapsed = Math.Max(0.0, nowMs - _lastUniqueTimestampMs);
        var rawInterpAlpha = DetectedSourceFps <= 30 && _duplicateStreak == 1
            ? 0.5f
            : Math.Clamp((float)(interpElapsed / Math.Max(8.0, _smoothedUniqueIntervalMs)), 0.05f, 0.95f);
        var interpAlpha = Math.Max(_lastPresentedAlpha, rawInterpAlpha);
        _lastPresentedAlpha = interpAlpha;
        _generatedFrames++;
        return new FramePacingDecision(false, true, interpAlpha, false);
    }
}
