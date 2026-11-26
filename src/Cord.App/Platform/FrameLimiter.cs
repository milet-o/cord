using Hexa.NET.SDL3;

namespace Cord.App.Platform;

/// <summary>
/// Caps the frame rate when presenting does not wait for the display. Some drivers ignore
/// VSync for Direct3D and Vulkan (seen with an NVIDIA GPU on a 144 Hz monitor, while OpenGL on
/// the same monitor was capped), and the loop then renders thousands of frames per second.
/// When VSync works, every frame already takes a refresh interval and the limiter never waits.
/// </summary>
internal sealed class FrameLimiter
{
    private const double DefaultRefreshHz = 60;

    // A little above the refresh rate, so a working VSync is never pushed past a vblank.
    private const double VSyncHeadroom = 1.05;

    // Without VSync frames may go out faster than the display refreshes, for latency, but not unbounded.
    private const double NoVSyncFactor = 2;

    private readonly Func<ulong> _now;
    private readonly Action<ulong> _delay;
    private ulong _lastFrame;

    public FrameLimiter(Func<ulong>? now = null, Action<ulong>? delay = null)
    {
        _now = now ?? SDL.GetTicksNS;
        _delay = delay ?? SDL.DelayPrecise;
    }

    public static ulong MinimumFrameNanoseconds(float refreshHz, bool vsync)
    {
        var hz = refreshHz > 0 ? refreshHz : DefaultRefreshHz;
        return (ulong)(1e9 / (hz * (vsync ? VSyncHeadroom : NoVSyncFactor)));
    }

    /// <summary>Call once per frame, after presenting; waits until the frame lasted at least the given time.</summary>
    public void Wait(ulong minimumFrameNanoseconds)
    {
        var now = _now();
        var elapsed = now - _lastFrame;
        if (_lastFrame != 0 && elapsed < minimumFrameNanoseconds)
        {
            _delay(minimumFrameNanoseconds - elapsed);
            now = _now();
        }

        _lastFrame = now;
    }
}
