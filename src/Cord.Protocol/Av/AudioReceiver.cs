using System.Buffers.Binary;

namespace Cord.Protocol.Av;

/// <summary>Audio stream parameters from STREAMINFO (docs/protocol/12-audio.md).</summary>
public sealed record AudioFormat(int Channels, int BitsPerSample, int SampleRate, int FrameSize)
{
    public const int HeaderSize = 14;

    public static AudioFormat Parse(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize)
            throw new InvalidDataException("Audio header is too short.");
        return new AudioFormat(
            header[0],
            header[1],
            (int)BinaryPrimitives.ReadUInt32BigEndian(header[2..]),
            (int)BinaryPrimitives.ReadUInt32BigEndian(header[6..]));
    }
}

public interface IAudioReceiverSink
{
    /// <summary>One Opus or haptic PCM unit, delivered once per frame index in its stream.</summary>
    void OnAudioFrame(ushort frameIndex, ReadOnlySpan<byte> opusFrame, bool haptics);
}

/// <summary>Expands audio packets into frames, using redundant copies to fill losses.</summary>
public sealed class AudioReceiver
{
    private readonly IAudioReceiverSink _sink;
    private readonly StreamState _audio = new();
    private readonly StreamState _haptics = new();

    public AudioReceiver(IAudioReceiverSink sink) => _sink = sink;

    public void OnPacket(AvPacket packet, ReadOnlySpan<byte> payload)
    {
        if (packet.Codec != AvPacket.OpusCodec || packet.UnitSize == 0)
            return;
        if (packet.SourceUnits + packet.FecUnits != packet.UnitsTotal || payload.Length != packet.UnitSize * packet.UnitsTotal)
            return;

        var state = packet.IsHaptics ? _haptics : _audio;
        var frames = new List<(ushort Index, int Unit)>();
        for (var unit = 0; unit < packet.UnitsTotal; unit++)
        {
            int index;
            if (unit < packet.SourceUnits)
            {
                index = packet.FrameIndex + unit;
            }
            else
            {
                index = packet.FrameIndex - packet.FecUnits + (unit - packet.SourceUnits);
                if (!state.Started && index < 0)
                    continue;
            }

            frames.Add(((ushort)index, unit));
        }

        foreach (var (index, unit) in frames.OrderBy(f => f.Index, Comparer<ushort>.Create(Compare)))
        {
            if (state.Started && Compare(index, state.Last) <= 0)
                continue;
            state.Started = true;
            state.Last = index;
            _sink.OnAudioFrame(index, payload.Slice(unit * packet.UnitSize, packet.UnitSize), packet.IsHaptics);
        }
    }

    private static int Compare(ushort a, ushort b) => unchecked((short)(a - b));

    private sealed class StreamState
    {
        public bool Started { get; set; }
        public ushort Last { get; set; }
    }
}
