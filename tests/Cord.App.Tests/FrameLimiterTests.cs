using Cord.App.Platform;

namespace Cord.App.Tests;

public sealed class FrameLimiterTests
{
    private const ulong Millisecond = 1_000_000;

    private ulong _now = 1000 * Millisecond;
    private readonly List<ulong> _delays = [];

    [Fact]
    public void FastFramesAreHeldToTheMinimumFrameTime()
    {
        var limiter = CreateLimiter();
        limiter.Wait(7 * Millisecond);

        _now += 1 * Millisecond;
        limiter.Wait(7 * Millisecond);

        Assert.Equal([6 * Millisecond], _delays);
    }

    [Fact]
    public void FramesThatAlreadyTookLongEnoughAreNotDelayed()
    {
        var limiter = CreateLimiter();
        limiter.Wait(7 * Millisecond);

        for (var i = 0; i < 5; i++)
        {
            // What a working VSync does: presenting blocks for one refresh interval.
            _now += 7 * Millisecond;
            limiter.Wait(FrameLimiter.MinimumFrameNanoseconds(144, vsync: true));
        }

        Assert.Empty(_delays);
    }

    [Theory]
    [InlineData(144f, true, 6613756)]
    [InlineData(60f, true, 15873015)]
    [InlineData(144f, false, 3472222)]
    [InlineData(0f, true, 15873015)]
    public void MinimumFrameTimeFollowsTheRefreshRate(float refreshHz, bool vsync, long expected)
    {
        Assert.Equal((ulong)expected, FrameLimiter.MinimumFrameNanoseconds(refreshHz, vsync));
    }

    private FrameLimiter CreateLimiter() => new(() => _now, delay =>
    {
        _delays.Add(delay);
        _now += delay;
    });
}
