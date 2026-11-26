using System.Buffers.Binary;

namespace Cord.Protocol;

/// <summary>The 8-byte PSN account id (docs/protocol/02-psn-account.md).</summary>
public readonly record struct PsnAccountId
{
    public const int Size = 8;

    private readonly ulong _value;

    private PsnAccountId(ulong value) => _value = value;

    public static PsnAccountId FromUserId(long userId) => new(unchecked((ulong)userId));

    public static PsnAccountId FromBase64(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        if (bytes.Length != Size)
            throw new FormatException($"A PSN account id has {Size} bytes.");
        return new PsnAccountId(BinaryPrimitives.ReadUInt64LittleEndian(bytes));
    }

    public byte[] ToBytes()
    {
        var bytes = new byte[Size];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, _value);
        return bytes;
    }

    public string ToBase64() => Convert.ToBase64String(ToBytes());

    public override string ToString() => ToBase64();
}
