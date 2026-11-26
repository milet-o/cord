using System.Buffers;
using Hexa.NET.SDL3;

namespace Cord.App.Audio;

/// <summary>Plays USB DualSense actuator PCM (docs/protocol/12-audio.md).</summary>
internal sealed unsafe class HapticsOutput : IDisposable
{
    private const int SourceRate = 3000;
    private const int PlaybackRate = 48000;
    private const int Channels = 4;
    private const int MaxQueuedBytes = SourceRate * Channels * sizeof(short) * 80 / 1000;

    private readonly object _gate = new();
    private SDLAudioStreamPtr _stream;
    private uint _device;
    private bool _enabled;

    public bool Available
    {
        get
        {
            lock (_gate)
                return !_stream.IsNull;
        }
    }

    /// <summary>Called by the render thread with the selected gamepad's USB/streaming state.</summary>
    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_enabled == enabled)
                return;
            _enabled = enabled;
            Close();
            if (_enabled)
                Open();
        }
    }

    public void Refresh()
    {
        lock (_gate)
        {
            Close();
            if (_enabled)
                Open();
        }
    }

    /// <summary>Called by the network thread; device changes and shutdown cannot race submission.</summary>
    public void Play(ReadOnlySpan<byte> pcm)
    {
        lock (_gate)
        {
            if (!_stream.IsNull)
                QueueFrame(_stream, pcm);
        }
    }

    public void Dispose() => SetEnabled(false);

    internal static SDLAudioStreamPtr CreateStream()
    {
        var source = new SDLAudioSpec { Format = SDLAudioFormat.S16Le, Channels = Channels, Freq = SourceRate };
        var destination = new SDLAudioSpec { Format = SDLAudioFormat.S16Le, Channels = Channels, Freq = PlaybackRate };
        return SDL.CreateAudioStream(&source, &destination);
    }

    internal static bool QueueFrame(SDLAudioStreamPtr stream, ReadOnlySpan<byte> pcm)
    {
        if (pcm.IsEmpty || pcm.Length % (2 * sizeof(short)) != 0)
            return false;
        var length = checked(pcm.Length * 2);
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var expanded = buffer.AsSpan(0, length);
            for (int input = 0, output = 0; input < pcm.Length; input += 4, output += 8)
            {
                expanded.Slice(output, 4).Clear();
                pcm.Slice(input, 4).CopyTo(expanded.Slice(output + 4, 4));
            }

            if (SDL.GetAudioStreamQueued(stream) > MaxQueuedBytes)
                SDL.ClearAudioStream(stream);
            fixed (byte* data = expanded)
                return SDL.PutAudioStreamData(stream, data, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Open()
    {
        int count;
        var devices = SDL.GetAudioPlaybackDevices(&count);
        if (devices == null)
            return;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var name = SDL.GetAudioDeviceNameS(devices[i]);
                if (name is null || !(name.Contains("DualSense", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (TryOpenDevice(devices[i]))
                    return;
            }
        }
        finally
        {
            SDL.Free(devices);
        }
    }

    private bool TryOpenDevice(uint id)
    {
        var requested = new SDLAudioSpec { Format = SDLAudioFormat.S16Le, Channels = Channels, Freq = PlaybackRate };
        _device = SDL.OpenAudioDevice(id, &requested);
        if (_device == 0)
            return false;

        SDLAudioSpec actual;
        if (!SDL.GetAudioDeviceFormat(_device, &actual, (int*)null) || actual.Channels != Channels || actual.Freq != PlaybackRate)
        {
            Close();
            return false;
        }

        _stream = CreateStream();
        if (_stream.IsNull || !SDL.BindAudioStream(_device, _stream) || !SDL.ResumeAudioDevice(_device))
        {
            Close();
            return false;
        }
        return true;
    }

    private void Close()
    {
        if (!_stream.IsNull)
            SDL.DestroyAudioStream(_stream);
        _stream = default;
        if (_device != 0)
            SDL.CloseAudioDevice(_device);
        _device = 0;
    }
}
