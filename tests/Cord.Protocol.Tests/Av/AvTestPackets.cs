using System.Buffers.Binary;
using Cord.Protocol.Av;

namespace Cord.Protocol.Tests.Av;

internal static class AvTestPackets
{
    public static byte[] Video(ushort packetIndex, ushort frameIndex, int unitIndex, int total, int fec, int adaptive, byte[] payload)
    {
        var datagram = new byte[0x15 + payload.Length];
        datagram[0] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(1), packetIndex);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(3), frameIndex);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(5), (uint)((unitIndex << 21) | ((total - 1) << 10) | fec));
        datagram[9] = 6;
        datagram[0x14] = (byte)(adaptive << 5);
        payload.CopyTo(datagram, 0x15);
        return datagram;
    }

    public static byte[] Audio(ushort frameIndex, int source, int fec, byte[][] units, bool haptics = false)
    {
        var unitSize = units[0].Length;
        var datagram = new byte[0x14 + (unitSize * units.Length)];
        datagram[0] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(3), frameIndex);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(5), (uint)(((units.Length - 1) << 16) | (unitSize << 8) | (fec << 4) | source));
        datagram[9] = AvPacket.OpusCodec;
        datagram[0x13] = haptics ? (byte)2 : (byte)0;
        for (var i = 0; i < units.Length; i++)
            units[i].CopyTo(datagram, 0x14 + (i * unitSize));
        return datagram;
    }

    /// <summary>Splits a frame into source units with padding fields plus FEC units.</summary>
    public static byte[][] EncodeFrame(byte[] frame, int sourceUnits, int fecUnits)
    {
        var chunk = (frame.Length + sourceUnits - 1) / sourceUnits;
        var unitSize = chunk + 2;
        var units = new Memory<byte>[sourceUnits + fecUnits];
        var lengths = new int[units.Length];
        for (var i = 0; i < units.Length; i++)
            units[i] = new byte[unitSize];

        for (var i = 0; i < sourceUnits; i++)
        {
            var part = frame.AsSpan(Math.Min(frame.Length, i * chunk), Math.Max(0, Math.Min(chunk, frame.Length - (i * chunk))));
            BinaryPrimitives.WriteUInt16BigEndian(units[i].Span, (ushort)(chunk - part.Length));
            part.CopyTo(units[i].Span[2..]);
            lengths[i] = 2 + part.Length;
        }

        CauchyFec.Encode(units, sourceUnits, fecUnits);
        for (var i = sourceUnits; i < units.Length; i++)
            lengths[i] = unitSize;

        return units.Select((u, i) => u[..lengths[i]].ToArray()).ToArray();
    }
}
