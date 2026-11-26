using Hexa.NET.SDL3;

namespace Cord.App.Audio;

/// <summary>
/// Plays interleaved 16-bit PCM through an SDL audio stream, dropping queued audio when the
/// queue grows beyond the latency budget.
/// </summary>
internal sealed unsafe class AudioOutput : IDisposable
{
    private const uint DefaultPlaybackDevice = 0xFFFFFFFF;
    private readonly SDLAudioStreamPtr _stream;
    private readonly int _maxQueuedBytes;

    public AudioOutput(int sampleRate, int channels, float volume = 1.0f, int maxQueuedLatencyMs = 80)
    {
        var spec = new SDLAudioSpec { Format = SDLAudioFormat.S16Le, Channels = channels, Freq = sampleRate };
        _stream = SDL.OpenAudioDeviceStream(DefaultPlaybackDevice, &spec, (SDLAudioStreamCallback?)null, null);
        if (_stream.IsNull)
            throw new InvalidOperationException("Could not open the audio device.");
        var latencySeconds = Math.Clamp(maxQueuedLatencyMs, 20, 200) / 1000.0;
        _maxQueuedBytes = (int)(sampleRate * channels * sizeof(short) * latencySeconds);
        SetVolume(volume);
        SDL.ResumeAudioStreamDevice(_stream);
    }

    public void SetVolume(float volume) =>
        SDL.SetAudioStreamGain(_stream, Math.Clamp(volume, 0f, 1f));

    /// <summary>Thread safe; called from the network thread.</summary>
    public void Play(ReadOnlySpan<short> samples)
    {
        if (SDL.GetAudioStreamQueued(_stream) > _maxQueuedBytes)
            SDL.ClearAudioStream(_stream);
        fixed (short* data = samples)
            SDL.PutAudioStreamData(_stream, data, samples.Length * sizeof(short));
    }

    public void Dispose() => SDL.DestroyAudioStream(_stream);
}
