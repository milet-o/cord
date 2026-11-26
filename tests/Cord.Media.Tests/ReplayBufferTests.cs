using Concentus;
using FFmpeg.AutoGen;

namespace Cord.Media.Tests;

public sealed class ReplayBufferTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cord-replay-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [FfmpegFact]
    public unsafe void SaveMp4_RemuxesH264AndOpusIntoValidPlayableMp4()
    {
        FfmpegLibrary.Load();
        var stream = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "testsrc-320x240.h264"));
        var units = FfmpegTestEnvironment.SplitAccessUnits(stream, VideoCodecKind.H264);
        Assert.NotEmpty(units);

        using var opusEncoder = OpusCodecFactory.CreateEncoder(48000, 2, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO);
        var pcm = new short[480 * 2];
        var opusBuf = new byte[512];
        var opusLen = opusEncoder.Encode(pcm, 480, opusBuf, opusBuf.Length);
        var opusPacket = opusBuf.AsSpan(0, opusLen);

        var replay = new ReplayBuffer(TimeSpan.FromSeconds(10));
        replay.Configure(VideoCodecKind.H264, 320, 240, 48000, 2, units[0]);

        for (var i = 0; i < units.Count; i++)
        {
            var ts = i * 33_333L;
            replay.AddVideoFrame(units[i], i == 0 ? units[0] : default, i == 0, ts);
            replay.AddAudioFrame(opusPacket, ts);
            replay.AddAudioFrame(opusPacket, ts + 10_000L);
        }

        Assert.True(replay.HasClipData);

        Directory.CreateDirectory(_directory);
        var outPath = Path.Combine(_directory, "clip.mp4");
        var savedPath = replay.SaveMp4(outPath);

        Assert.Equal(outPath, savedPath);
        Assert.True(File.Exists(savedPath));
        var fileBytes = File.ReadAllBytes(savedPath);
        Assert.True(fileBytes.Length > 200);
        Assert.Equal("ftyp"u8.ToArray(), fileBytes[4..8]);

        AVFormatContext* probeCtx = null;
        try
        {
            Assert.True(ffmpeg.avformat_open_input(&probeCtx, savedPath, null, null) >= 0);
            Assert.True(ffmpeg.avformat_find_stream_info(probeCtx, null) >= 0);
            Assert.Equal(2u, probeCtx->nb_streams);
        }
        finally
        {
            if (probeCtx != null)
                ffmpeg.avformat_close_input(&probeCtx);
        }
    }

    [Fact]
    public void AddVideoFrame_PrunesOldFramesWhilePreservingKeyframeBoundary()
    {
        var replay = new ReplayBuffer(TimeSpan.FromSeconds(2));
        replay.Configure(VideoCodecKind.H264, 1920, 1080, 48000, 2, [0, 0, 0, 1, 0x67]);

        replay.AddVideoFrame([0, 0, 0, 1, 0x65, 1], default, isKeyframe: true, timestampUs: 0);
        replay.AddVideoFrame([0, 0, 0, 1, 0x41, 2], default, isKeyframe: false, timestampUs: 1_000_000);
        replay.AddVideoFrame([0, 0, 0, 1, 0x65, 3], default, isKeyframe: true, timestampUs: 5_000_000);

        Assert.True(replay.HasClipData);
    }

    [Fact]
    public void Prune_PreservesSoleKeyframePastWindow_AndRetainsAnchorKeyframeWithPeriodicIdrs()
    {
        var infiniteGop = new ReplayBuffer(TimeSpan.FromSeconds(2));
        byte[] header = [0, 0, 0, 1, 0x67, 0x4D, 0x00, 0x2A];
        infiniteGop.Configure(VideoCodecKind.H264, 1920, 1080, 48000, 2, header);

        // Single IDR at t=0 followed only by P-frames past 2 seconds (PS5 infinite GOP)
        infiniteGop.AddVideoFrame([0, 0, 0, 1, 0x65, 1], default, isKeyframe: true, timestampUs: 0);
        for (var i = 1; i <= 30; i++)
            infiniteGop.AddVideoFrame([0, 0, 0, 1, 0x41, (byte)i], default, isKeyframe: false, timestampUs: i * 100_000L);

        Assert.True(infiniteGop.HasClipData);

        // Periodic keyframes at 1s intervals through 6s: pruning must always keep an anchor keyframe <= cutoff
        var periodic = new ReplayBuffer(TimeSpan.FromSeconds(2));
        periodic.Configure(VideoCodecKind.H264, 1920, 1080, 48000, 2, header);
        for (var i = 0; i <= 60; i++)
        {
            var ts = i * 100_000L;
            var isKey = i % 10 == 0;
            periodic.AddVideoFrame([0, 0, 0, 1, (byte)(isKey ? 0x65 : 0x41), (byte)i], default, isKey, ts);
            periodic.AddAudioFrame([0xAA, 0xBB], ts);
        }

        Assert.True(periodic.HasClipData);
    }
}
