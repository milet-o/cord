using Cord.App.Video;
using Cord.Core.Settings;

namespace Cord.App.Tests;

public sealed class FramePacerTests
{
    [Fact]
    public void DetectsThirtyFpsDuplicateCadenceAndGeneratesIntermediateFrames()
    {
        var pacer = new FramePacer();
        var t = 100.0;

        // First frame initializes baseline
        pacer.OnNewSourceFrame(0.05f, t, FrameGenMode.LowLatencyExtrapolation, out _);

        // Alternate unique (high MAD) and duplicate (near-zero MAD) frames at 60 Hz (every 16.67 ms)
        for (var i = 1; i <= 12; i++)
        {
            t += 16.67;
            var isDup = (i % 2) == 1;
            var mad = isDup ? 0.0004f : 0.04f;
            var shouldComputeFlow = pacer.OnNewSourceFrame(mad, t, FrameGenMode.LowLatencyExtrapolation, out var sceneCut);
            Assert.False(sceneCut);
            if (i >= 6)
                Assert.Equal(!isDup, shouldComputeFlow);
        }

        Assert.Equal(30, pacer.DetectedSourceFps);
        Assert.Equal("Extrapolation 30->60", pacer.StatusLabel(FrameGenMode.LowLatencyExtrapolation, 60f));

        // On unique frame: zero-queue extrapolation presents real frame (UseFrameGen=false)
        var realTick = pacer.EvaluateRenderTick(FrameGenMode.LowLatencyExtrapolation, 60f, t, isFreshUniqueFrameThisTick: true);
        Assert.False(realTick.UseFrameGen);
        Assert.Equal(0f, realTick.Alpha);

        // Next frame is a single isolated duplicate -> should extrapolate forward at alpha=0.5
        t += 16.67;
        pacer.OnNewSourceFrame(0.0004f, t, FrameGenMode.LowLatencyExtrapolation, out _);
        Assert.False(pacer.IsStaticScene);
        var genTick = pacer.EvaluateRenderTick(FrameGenMode.LowLatencyExtrapolation, 60f, t, isFreshUniqueFrameThisTick: false);
        Assert.True(genTick.UseFrameGen);
        Assert.Equal(0.5f, genTick.Alpha);
    }

    [Fact]
    public void ConsecutiveDuplicatesStaticSceneDoesNotTriggerThirtyFpsOrFrameGen()
    {
        var pacer = new FramePacer();
        var t = 100.0;
        pacer.OnNewSourceFrame(0.05f, t, FrameGenMode.LowLatencyExtrapolation, out _);

        // Player stops moving: many consecutive near-identical frames arrive
        for (var i = 0; i < 12; i++)
        {
            t += 16.67;
            var runFlow = pacer.OnNewSourceFrame(0.0004f, t, FrameGenMode.LowLatencyExtrapolation, out _);
            Assert.False(runFlow);
        }

        Assert.True(pacer.IsStaticScene);
        Assert.Equal(60, pacer.DetectedSourceFps);
        Assert.Equal("Static (passthrough)", pacer.StatusLabel(FrameGenMode.LowLatencyExtrapolation, 144f));

        var decision = pacer.EvaluateRenderTick(FrameGenMode.LowLatencyExtrapolation, 144f, t + 8.0, isFreshUniqueFrameThisTick: false);
        Assert.False(decision.UseFrameGen);
        Assert.False(decision.PresentPreviousFrame);
    }

    [Fact]
    public void SmoothInterpolationAlphaIsMonotonicAcrossJitteredTicks()
    {
        var pacer = new FramePacer();
        var t = 100.0;
        for (var i = 0; i < 8; i++)
        {
            t += 16.67;
            pacer.OnNewSourceFrame(0.04f, t, FrameGenMode.SmoothInterpolation, out _);
        }

        // Fresh unique frame starts at alpha 0
        var first = pacer.EvaluateRenderTick(FrameGenMode.SmoothInterpolation, 144f, t, isFreshUniqueFrameThisTick: true);
        Assert.True(first.PresentPreviousFrame);
        Assert.Equal(0f, first.Alpha);

        var mid = pacer.EvaluateRenderTick(FrameGenMode.SmoothInterpolation, 144f, t + 9.0, isFreshUniqueFrameThisTick: false);
        Assert.True(mid.UseFrameGen);
        Assert.InRange(mid.Alpha, 0.4f, 0.65f);

        // Even if clock/jitter ticks slightly earlier, alpha must never step backward
        var earlierJitter = pacer.EvaluateRenderTick(FrameGenMode.SmoothInterpolation, 144f, t + 6.0, isFreshUniqueFrameThisTick: false);
        Assert.True(earlierJitter.UseFrameGen);
        Assert.True(earlierJitter.Alpha >= mid.Alpha);
    }

    [Fact]
    public void SixtyFpsSourceOnSixtyHzDisplayUsesPassthrough()
    {
        var pacer = new FramePacer();
        var t = 0.0;
        for (var i = 0; i < 10; i++)
        {
            t += 16.67;
            pacer.OnNewSourceFrame(0.04f, t, FrameGenMode.SmoothInterpolation, out _);
        }

        Assert.Equal(60, pacer.DetectedSourceFps);
        Assert.Equal("Passthrough (60 Hz)", pacer.StatusLabel(FrameGenMode.SmoothInterpolation, 60f));
        var decision = pacer.EvaluateRenderTick(FrameGenMode.SmoothInterpolation, 60f, t + 8.0, isFreshUniqueFrameThisTick: false);
        Assert.False(decision.UseFrameGen);
    }

    [Fact]
    public void SixtyFpsSourceOnHighRefreshDisplayInterpolatesBetweenFrames()
    {
        var pacer = new FramePacer();
        var t = 0.0;
        for (var i = 0; i < 10; i++)
        {
            t += 16.67;
            pacer.OnNewSourceFrame(0.04f, t, FrameGenMode.SmoothInterpolation, out _);
        }

        Assert.Equal("Interpolation 60->144", pacer.StatusLabel(FrameGenMode.SmoothInterpolation, 144f));

        var onUnique = pacer.EvaluateRenderTick(FrameGenMode.SmoothInterpolation, 144f, t, isFreshUniqueFrameThisTick: true);
        Assert.True(onUnique.PresentPreviousFrame);
        Assert.False(onUnique.UseFrameGen);

        var between = pacer.EvaluateRenderTick(FrameGenMode.SmoothInterpolation, 144f, t + 8.33, isFreshUniqueFrameThisTick: false);
        Assert.True(between.UseFrameGen);
        Assert.InRange(between.Alpha, 0.4f, 0.6f);
    }

    [Fact]
    public void SceneCutSuppressesFrameGenerationForThatFrame()
    {
        var pacer = new FramePacer();
        pacer.OnNewSourceFrame(0.04f, 100.0, FrameGenMode.LowLatencyExtrapolation, out _);
        pacer.OnNewSourceFrame(0.04f, 116.7, FrameGenMode.LowLatencyExtrapolation, out _);

        var runFlow = pacer.OnNewSourceFrame(0.35f, 133.4, FrameGenMode.LowLatencyExtrapolation, out var sceneCut);
        Assert.True(sceneCut);
        Assert.False(runFlow);

        var decision = pacer.EvaluateRenderTick(FrameGenMode.LowLatencyExtrapolation, 144f, 140.0, isFreshUniqueFrameThisTick: false);
        Assert.False(decision.UseFrameGen);
    }
}
