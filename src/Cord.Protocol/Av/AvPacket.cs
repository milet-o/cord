using System.Buffers.Binary;
using Cord.Protocol.Takion;

namespace Cord.Protocol.Av;

/// <summary>Header of a Takion v12 video or audio packet (docs/protocol/11-video.md).</summary>
public readonly record struct AvPacket(
    bool IsVideo,
    ushort PacketIndex,
    ushort FrameIndex,
    int UnitIndex,
    int UnitsTotal,
    int FecUnits,
    int SourceUnits,
    int UnitSize,
    byte Codec,
    int AdaptiveStreamIndex,
    bool IsHaptics,
    int PayloadOffset)
{
    public const byte OpusCodec = 5;

    private const int NaluInfoSize = 3;

    public static bool TryParse(ReadOnlySpan<byte> datagram, out AvPacket packet)
    {
        packet = default;
        if (datagram.Length < 0x14)
            return false;

        var type = TakionPackets.TypeOf(datagram);
        if (type is not (TakionPacketType.Video or TakionPacketType.Audio))
            return false;

        var isVideo = type == TakionPacketType.Video;
        var hasNaluInfo = (datagram[0] & 0x10) != 0;
        var units = BinaryPrimitives.ReadUInt32BigEndian(datagram[5..]);

        int unitIndex, total, fec, source = 0, unitSize = 0, adaptive = 0, offset;
        var haptics = false;
        if (isVideo)
        {
            unitIndex = (int)((units >> 21) & 0x7FF);
            total = (int)((units >> 10) & 0x7FF) + 1;
            fec = (int)(units & 0x3FF);
            adaptive = datagram[0x14] >> 5;
            offset = 0x15 + (hasNaluInfo ? NaluInfoSize : 0);
        }
        else
        {
            unitIndex = (int)((units >> 24) & 0xFF);
            total = (int)((units >> 16) & 0xFF) + 1;
            unitSize = (int)((units >> 8) & 0xFF);
            fec = (int)((units >> 4) & 0x0F);
            source = (int)(units & 0x0F);
            var hapticsOffset = 0x13 + (hasNaluInfo ? NaluInfoSize : 0);
            if (datagram.Length <= hapticsOffset)
                return false;
            haptics = datagram[hapticsOffset] == 0x02;
            offset = hapticsOffset + 1;
        }

        if (offset > datagram.Length)
            return false;

        if (isVideo)
            source = total - fec;

        packet = new AvPacket(
            isVideo,
            BinaryPrimitives.ReadUInt16BigEndian(datagram[1..]),
            BinaryPrimitives.ReadUInt16BigEndian(datagram[3..]),
            unitIndex,
            total,
            fec,
            source,
            unitSize,
            datagram[9],
            adaptive,
            haptics,
            offset);
        return true;
    }
}
