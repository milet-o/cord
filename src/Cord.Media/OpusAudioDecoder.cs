using Concentus;

namespace Cord.Media;

/// <summary>Decodes the console's Opus frames to interleaved 16-bit PCM (docs/protocol/12-audio.md).</summary>
public sealed class OpusAudioDecoder : IDisposable
{
    private const int MaxFrameMilliseconds = 120;

    private readonly IOpusDecoder _decoder;
    private readonly short[] _pcm;

    public OpusAudioDecoder(int sampleRate, int channels)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _decoder = OpusCodecFactory.CreateDecoder(sampleRate, channels);
        _pcm = new short[sampleRate * MaxFrameMilliseconds / 1000 * channels];
    }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>Returns the decoded samples; the buffer is reused by the next call.</summary>
    public ReadOnlySpan<short> Decode(ReadOnlySpan<byte> opusFrame)
    {
        var samplesPerChannel = _decoder.Decode(opusFrame, _pcm, _pcm.Length / Channels);
        return _pcm.AsSpan(0, samplesPerChannel * Channels);
    }

    /// <summary>Synthesises audio for a lost frame using Opus packet loss concealment.</summary>
    public ReadOnlySpan<short> Conceal(int samplesPerChannel)
    {
        var decoded = _decoder.Decode(ReadOnlySpan<byte>.Empty, _pcm, samplesPerChannel);
        return _pcm.AsSpan(0, decoded * Channels);
    }

    public void Dispose() => _decoder.Dispose();
}
