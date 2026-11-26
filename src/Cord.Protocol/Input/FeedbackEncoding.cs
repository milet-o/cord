using System.Buffers.Binary;

namespace Cord.Protocol.Input;

/// <summary>Wire encoding of controller input (docs/protocol/13-input.md).</summary>
public static class FeedbackEncoding
{
    public const int StateSize = 0x1C;

    private const float GyroRange = 30f;
    private const float AccelRange = 5f;
    private const float QuaternionLimit = 0.70710677f;

    public static void WriteState(in ControllerState state, Span<byte> output)
    {
        var motion = state.Motion;
        output[0] = 0xA0;
        WriteScaled(output[0x01..], motion.GyroX, GyroRange);
        WriteScaled(output[0x03..], motion.GyroY, GyroRange);
        WriteScaled(output[0x05..], motion.GyroZ, GyroRange);
        WriteScaled(output[0x07..], motion.AccelX, AccelRange);
        WriteScaled(output[0x09..], motion.AccelY, AccelRange);
        WriteScaled(output[0x0B..], motion.AccelZ, AccelRange);
        BinaryPrimitives.WriteUInt32LittleEndian(output[0x0D..], CompressQuaternion(motion.OrientX, motion.OrientY, motion.OrientZ, motion.OrientW));
        BinaryPrimitives.WriteInt16BigEndian(output[0x11..], state.LeftX);
        BinaryPrimitives.WriteInt16BigEndian(output[0x13..], state.LeftY);
        BinaryPrimitives.WriteInt16BigEndian(output[0x15..], state.RightX);
        BinaryPrimitives.WriteInt16BigEndian(output[0x17..], state.RightY);
        output[0x19] = 0;
        output[0x1A] = 0;
        output[0x1B] = 1;
    }

    /// <summary>"Smallest three" compression into 32 bits.</summary>
    public static uint CompressQuaternion(float x, float y, float z, float w)
    {
        ReadOnlySpan<float> q = [x, y, z, w];
        var largest = 0;
        for (var i = 1; i < 4; i++)
        {
            if (MathF.Abs(q[i]) > MathF.Abs(q[largest]))
                largest = i;
        }

        var result = (q[largest] < 0 ? 1u : 0u) | ((uint)largest << 1);
        var field = 0;
        for (var i = 0; i < 4; i++)
        {
            if (i == largest)
                continue;
            var value = Math.Clamp(q[i], -QuaternionLimit, QuaternionLimit) + QuaternionLimit;
            var quantized = (uint)(value * (0x1FF / (2f * QuaternionLimit)));
            result |= quantized << (3 + (field * 9));
            field++;
        }

        return result;
    }

    public static HistoryEvent ButtonEvent(ControllerButtons button, bool pressed) => button switch
    {
        ControllerButtons.Options => TwoByte(pressed ? 0xAC : 0x8C),
        ControllerButtons.Create => TwoByte(pressed ? 0xAD : 0x8D),
        ControllerButtons.PS => TwoByte(pressed ? 0xAE : 0x8E),
        ControllerButtons.L3 => TwoByte(pressed ? 0xAF : 0x8F),
        ControllerButtons.R3 => TwoByte(pressed ? 0xB0 : 0x90),
        ControllerButtons.Touchpad => TwoByte(pressed ? 0xB1 : 0x91),
        _ => new HistoryEvent([0x80, DigitalCode(button), pressed ? (byte)0xFF : (byte)0x00]),
    };

    public static HistoryEvent TriggerEvent(bool right, byte value) => new([0x80, right ? (byte)0x87 : (byte)0x86, value]);

    public static HistoryEvent TouchEvent(bool down, byte pointerId, ushort x, ushort y) => new(
    [
        down ? (byte)0xD0 : (byte)0xC0,
        (byte)(pointerId & 0x7F),
        (byte)(x >> 4),
        (byte)(((x & 0x0F) << 4) | ((y >> 8) & 0x0F)),
        (byte)y,
    ]);

    private static HistoryEvent TwoByte(int code) => new([0x80, (byte)code]);

    private static byte DigitalCode(ControllerButtons button) => button switch
    {
        ControllerButtons.DpadUp => 0x80,
        ControllerButtons.DpadDown => 0x81,
        ControllerButtons.DpadLeft => 0x82,
        ControllerButtons.DpadRight => 0x83,
        ControllerButtons.L1 => 0x84,
        ControllerButtons.R1 => 0x85,
        ControllerButtons.Cross => 0x88,
        ControllerButtons.Circle => 0x89,
        ControllerButtons.Square => 0x8A,
        ControllerButtons.Triangle => 0x8B,
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Not a single digital button."),
    };

    private static void WriteScaled(Span<byte> output, float value, float range)
    {
        var normalized = (Math.Clamp(value, -range, range) + range) / (2 * range);
        BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)(normalized * 0xFFFF));
    }
}

public sealed record HistoryEvent(byte[] Bytes);
