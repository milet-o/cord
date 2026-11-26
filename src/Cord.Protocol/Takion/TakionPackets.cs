using System.Buffers.Binary;
using Cord.Protocol.Crypto;

namespace Cord.Protocol.Takion;

public readonly ref struct ControlChunk
{
    public ControlChunk(uint tag, uint keyPosLow, TakionChunkType type, byte flags, ReadOnlySpan<byte> payload)
    {
        Tag = tag;
        KeyPosLow = keyPosLow;
        Type = type;
        Flags = flags;
        Payload = payload;
    }

    public uint Tag { get; }
    public uint KeyPosLow { get; }
    public TakionChunkType Type { get; }
    public byte Flags { get; }
    public ReadOnlySpan<byte> Payload { get; }
}

public readonly record struct InitParameters(uint Tag, uint ReceiveWindow, ushort OutboundStreams, ushort InboundStreams, uint InitialSequenceNumber);

/// <summary>Takion wire formats (docs/protocol/07-takion.md).</summary>
public static class TakionPackets
{
    public const int ControlHeaderSize = 17;
    public const int InitPayloadSize = 16;
    public const int CookieSize = 32;
    public const int DataHeaderSize = 9;
    public const int DataContinuationHeaderSize = 8;
    public const int DataAckPayloadSize = 12;
    public const int CongestionPacketSize = 15;
    public const int FeedbackHeaderSize = 12;

    public static TakionPacketType TypeOf(ReadOnlySpan<byte> datagram) => (TakionPacketType)(datagram[0] & 0x0F);

    public static byte[] BuildControl(uint tag, TakionChunkType type, byte flags, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[ControlHeaderSize + payload.Length];
        packet[0] = (byte)TakionPacketType.Control;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), tag);
        packet[0x0D] = (byte)type;
        packet[0x0E] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0x0F), (ushort)(payload.Length + 4));
        payload.CopyTo(packet.AsSpan(ControlHeaderSize));
        return packet;
    }

    public static bool TryParseControl(ReadOnlySpan<byte> datagram, out ControlChunk chunk)
    {
        chunk = default;
        if (datagram.Length < ControlHeaderSize || TypeOf(datagram) != TakionPacketType.Control)
            return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(datagram[0x0F..]);
        if (length < 4 || datagram.Length != length + 13)
            return false;

        chunk = new ControlChunk(
            BinaryPrimitives.ReadUInt32BigEndian(datagram[1..]),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[9..]),
            (TakionChunkType)datagram[0x0D],
            datagram[0x0E],
            datagram[ControlHeaderSize..]);
        return true;
    }

    public static byte[] BuildInit(InitParameters init)
    {
        var payload = new byte[InitPayloadSize];
        WriteInit(payload, init);
        return payload;
    }

    public static byte[] BuildInitAck(InitParameters init, ReadOnlySpan<byte> cookie)
    {
        var payload = new byte[InitPayloadSize + CookieSize];
        WriteInit(payload, init);
        cookie.CopyTo(payload.AsSpan(InitPayloadSize));
        return payload;
    }

    public static InitParameters ReadInit(ReadOnlySpan<byte> payload) => new(
        BinaryPrimitives.ReadUInt32BigEndian(payload),
        BinaryPrimitives.ReadUInt32BigEndian(payload[4..]),
        BinaryPrimitives.ReadUInt16BigEndian(payload[8..]),
        BinaryPrimitives.ReadUInt16BigEndian(payload[10..]),
        BinaryPrimitives.ReadUInt32BigEndian(payload[12..]));

    public static byte[] BuildData(uint sequenceNumber, ushort channel, TakionDataType? dataType, ReadOnlySpan<byte> data)
    {
        var headerSize = dataType is null ? DataContinuationHeaderSize : DataHeaderSize;
        var payload = new byte[headerSize + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload, sequenceNumber);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4), channel);
        if (dataType is not null)
            payload[8] = (byte)dataType;
        data.CopyTo(payload.AsSpan(headerSize));
        return payload;
    }

    public static byte[] BuildDataAck(uint cumulativeSequenceNumber, uint receiveWindow)
    {
        var payload = new byte[DataAckPayloadSize];
        BinaryPrimitives.WriteUInt32BigEndian(payload, cumulativeSequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), receiveWindow);
        return payload;
    }

    public static (int GmacOffset, int KeyPosOffset, bool ZeroKeyPos)? AuthenticationLayout(TakionPacketType type) => type switch
    {
        TakionPacketType.Control => (0x05, 0x09, true),
        TakionPacketType.Video or TakionPacketType.Audio => (0x0A, 0x0E, false),
        TakionPacketType.Congestion => (0x07, 0x0B, true),
        TakionPacketType.FeedbackState or TakionPacketType.FeedbackHistory => (0x08, 0x04, false),
        _ => null,
    };

    /// <summary>Writes key_pos and the GMAC of the datagram in place.</summary>
    public static void Sign(GkCrypt crypt, Span<byte> datagram, ulong keyPos)
    {
        var layout = AuthenticationLayout(TypeOf(datagram)) ?? throw new ArgumentException("Packet type is not authenticated.", nameof(datagram));
        var keyPosField = datagram.Slice(layout.KeyPosOffset, 4);
        var gmacField = datagram.Slice(layout.GmacOffset, GkCrypt.GmacSize);

        gmacField.Clear();
        if (layout.ZeroKeyPos)
            keyPosField.Clear();
        else
            BinaryPrimitives.WriteUInt32BigEndian(keyPosField, (uint)keyPos);

        Span<byte> gmac = stackalloc byte[GkCrypt.GmacSize];
        crypt.ComputeGmac(keyPos, datagram, gmac);
        gmac.CopyTo(gmacField);
        BinaryPrimitives.WriteUInt32BigEndian(keyPosField, (uint)keyPos);
    }

    /// <summary>Checks the GMAC of a received datagram. The datagram is restored afterwards.</summary>
    public static bool Verify(GkCrypt crypt, Span<byte> datagram, ulong keyPos)
    {
        var layout = AuthenticationLayout(TypeOf(datagram));
        if (layout is null || datagram.Length < layout.Value.KeyPosOffset + 4)
            return false;

        var keyPosField = datagram.Slice(layout.Value.KeyPosOffset, 4);
        var gmacField = datagram.Slice(layout.Value.GmacOffset, GkCrypt.GmacSize);
        Span<byte> received = stackalloc byte[GkCrypt.GmacSize];
        Span<byte> savedKeyPos = stackalloc byte[4];
        gmacField.CopyTo(received);
        keyPosField.CopyTo(savedKeyPos);

        gmacField.Clear();
        if (layout.Value.ZeroKeyPos)
            keyPosField.Clear();

        Span<byte> expected = stackalloc byte[GkCrypt.GmacSize];
        crypt.ComputeGmac(keyPos, datagram, expected);

        savedKeyPos.CopyTo(keyPosField);
        received.CopyTo(gmacField);
        return expected.SequenceEqual(received);
    }

    public static uint ReadKeyPosLow(ReadOnlySpan<byte> datagram)
    {
        var layout = AuthenticationLayout(TypeOf(datagram)) ?? throw new ArgumentException("Packet type has no key position.", nameof(datagram));
        return BinaryPrimitives.ReadUInt32BigEndian(datagram[layout.KeyPosOffset..]);
    }

    private static void WriteInit(Span<byte> payload, InitParameters init)
    {
        BinaryPrimitives.WriteUInt32BigEndian(payload, init.Tag);
        BinaryPrimitives.WriteUInt32BigEndian(payload[4..], init.ReceiveWindow);
        BinaryPrimitives.WriteUInt16BigEndian(payload[8..], init.OutboundStreams);
        BinaryPrimitives.WriteUInt16BigEndian(payload[10..], init.InboundStreams);
        BinaryPrimitives.WriteUInt32BigEndian(payload[12..], init.InitialSequenceNumber);
    }
}
