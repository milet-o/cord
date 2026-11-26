namespace Cord.Protocol.Av;

/// <summary>Arithmetic in GF(2^8) with the polynomial x^8 + x^4 + x^3 + x^2 + 1.</summary>
internal static class Gf256
{
    private const int Polynomial = 0x11D;

    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static Gf256()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if (x >= 256)
                x ^= Polynomial;
        }

        for (var i = 255; i < Exp.Length; i++)
            Exp[i] = Exp[i - 255];
    }

    public static byte Multiply(byte a, byte b) =>
        a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    public static byte Inverse(byte a) =>
        a == 0 ? throw new DivideByZeroException() : Exp[255 - Log[a]];

    public static byte Divide(byte a, byte b) => Multiply(a, Inverse(b));

    /// <summary>destination ^= factor * source, byte by byte.</summary>
    public static void MultiplyAdd(Span<byte> destination, ReadOnlySpan<byte> source, byte factor)
    {
        if (factor == 0)
            return;
        var logFactor = Log[factor];
        for (var i = 0; i < destination.Length; i++)
        {
            var value = source[i];
            if (value != 0)
                destination[i] ^= Exp[Log[value] + logFactor];
        }
    }
}
