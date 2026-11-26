using Concentus;

namespace Cord.Media.Tests;

public class MediaTests
{
    [FfmpegTheory]
    [InlineData(VideoCodecKind.H264, "testsrc-320x240.h264", HardwareAcceleration.None)]
    [InlineData(VideoCodecKind.Hevc, "testsrc-320x240.hevc", HardwareAcceleration.None)]
    [InlineData(VideoCodecKind.H264, "testsrc-320x240.h264", HardwareAcceleration.Auto)]
    [InlineData(VideoCodecKind.Hevc, "testsrc-320x240.hevc", HardwareAcceleration.Auto)]
    public void VideoDecoder_DecodesAccessUnits(VideoCodecKind codec, string fixture, HardwareAcceleration acceleration)
    {
        var stream = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        var units = FfmpegTestEnvironment.SplitAccessUnits(stream, codec);
        using var decoder = new VideoDecoder(codec, new VideoDecoderOptions(acceleration));

        var frames = new List<(int Width, int Height, VideoPixelFormat Format, int Planes, bool Hardware)>();
        foreach (var unit in units)
            decoder.Decode(unit, new DelegateSink(f => frames.Add((f.Width, f.Height, f.Format, f.Planes.Count, f.HardwareDecoded))));

        Assert.Equal(10, units.Count);
        Assert.InRange(frames.Count, 9, 10);
        Assert.All(frames, f =>
        {
            Assert.Equal(320, f.Width);
            Assert.Equal(240, f.Height);
            Assert.Equal(f.Format == VideoPixelFormat.Yuv420P ? 3 : 2, f.Planes);
            Assert.Equal(decoder.HardwareDevice is not null, f.Hardware);
        });
        if (acceleration == HardwareAcceleration.None)
            Assert.Null(decoder.HardwareDevice);
    }

    [FfmpegFact]
    public void VideoDecoder_IgnoresGarbage()
    {
        using var decoder = new VideoDecoder(VideoCodecKind.H264, new VideoDecoderOptions(HardwareAcceleration.None));
        var frames = 0;

        decoder.Decode([0, 0, 0, 1, 0xFF, 0xFF, 0x12], new DelegateSink(_ => frames++));

        Assert.Equal(0, frames);
    }

    [Fact]
    public void OpusAudioDecoder_DecodesEncodedTone()
    {
        const int rate = 48000, channels = 2, frameSize = 480;
        using var encoder = OpusCodecFactory.CreateEncoder(rate, channels, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO);
        using var decoder = new OpusAudioDecoder(rate, channels);
        var pcm = new short[frameSize * channels];
        for (var i = 0; i < frameSize; i++)
            pcm[2 * i] = pcm[(2 * i) + 1] = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000);
        var packet = new byte[1500];

        var length = encoder.Encode(pcm, frameSize, packet, packet.Length);
        var decoded = decoder.Decode(packet.AsSpan(0, length));

        Assert.Equal(frameSize * channels, decoded.Length);
        Assert.Equal(frameSize * channels, decoder.Conceal(frameSize).Length);
    }
}
