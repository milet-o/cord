using System.Buffers.Binary;
using Cord.Protocol.Input;

namespace Cord.WebShare;

/// <summary>Binary WebSocket framing for video, audio, rumble, and remote controller input.</summary>
public static class WebMediaProtocol
{
    public const byte VideoFrame = 0x01;
    public const byte AudioFrame = 0x02;
    public const byte Rumble = 0x03;
    public const byte ControllerInput = 0x10;

    public const byte VideoFlagKeyframe = 0x01;

    public const int MediaHeaderSize = 10;
    public const int RumblePacketSize = 3;
    public const int ControllerInputSize = 15;

    public static byte[] BuildVideoPacket(
        ReadOnlySpan<byte> frame,
        ReadOnlySpan<byte> prependHeader,
        bool isKeyframe,
        long timestampUs)
    {
        var packet = new byte[MediaHeaderSize + prependHeader.Length + frame.Length];
        packet[0] = VideoFrame;
        packet[1] = isKeyframe ? VideoFlagKeyframe : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(2, 8), timestampUs);
        if (!prependHeader.IsEmpty)
            prependHeader.CopyTo(packet.AsSpan(MediaHeaderSize));
        frame.CopyTo(packet.AsSpan(MediaHeaderSize + prependHeader.Length));
        return packet;
    }

    public static byte[] BuildAudioPacket(ReadOnlySpan<byte> opusFrame, long timestampUs)
    {
        var packet = new byte[MediaHeaderSize + opusFrame.Length];
        packet[0] = AudioFrame;
        packet[1] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(2, 8), timestampUs);
        opusFrame.CopyTo(packet.AsSpan(MediaHeaderSize));
        return packet;
    }

    public static byte[] BuildRumblePacket(byte left, byte right) => [Rumble, left, right];

    public static bool TryParseControllerInput(ReadOnlySpan<byte> payload, out ControllerState state)
    {
        if (payload.Length != ControllerInputSize || payload[0] != ControllerInput)
        {
            state = ControllerState.Idle;
            return false;
        }

        var buttons = (ControllerButtons)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));
        var l2 = payload[5];
        var r2 = payload[6];
        var leftX = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(7, 2));
        var leftY = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(9, 2));
        var rightX = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(11, 2));
        var rightY = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(13, 2));

        state = new ControllerState(
            buttons,
            l2,
            r2,
            leftX,
            leftY,
            rightX,
            rightY,
            TouchPoint.None,
            TouchPoint.None,
            MotionState.Neutral);
        return true;
    }
}
