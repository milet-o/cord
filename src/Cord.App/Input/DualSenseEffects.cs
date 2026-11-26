using Cord.Protocol.Stream;

namespace Cord.App.Input;

/// <summary>SDL's DualSense effects body, without HID framing (docs/protocol/13-input.md).</summary>
internal static class DualSenseEffects
{
    public const int Size = 47;

    public static void Write(Span<byte> destination, TriggerEffect left, TriggerEffect right)
    {
        if (left.Parameters is not { Length: 10 } || right.Parameters is not { Length: 10 })
            throw new ArgumentException("Each trigger effect must have exactly ten parameters.");
        Initialize(destination);
        destination[10] = right.Type;
        right.Parameters.CopyTo(destination[11..21]);
        destination[21] = left.Type;
        left.Parameters.CopyTo(destination[22..32]);
    }

    public static void WriteReset(Span<byte> destination)
    {
        Initialize(destination);
        destination[10] = destination[21] = 0x05;
    }

    private static void Initialize(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, Size);
        destination[..Size].Clear();
        destination[0] = 0x0C;
    }
}
