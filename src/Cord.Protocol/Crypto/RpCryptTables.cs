using System.Security.Cryptography;

namespace Cord.Protocol.Crypto;

/// <summary>
/// Constant tables of the Remote Play protocol, embedded as rpcrypt-tables.bin
/// (see docs/protocol/03-rpcrypt.md).
/// </summary>
public sealed class RpCryptTables
{
    private const string ResourceName = "Cord.Protocol.Crypto.rpcrypt-tables.bin";
    private const int HmacKeySize = 16;
    private const int RegistTableSize = 512;
    private const int SessionTableSize = 0x70 * 0x20;
    private const int TotalSize = HmacKeySize + (2 * RegistTableSize) + (2 * SessionTableSize);

    private static readonly byte[] ExpectedSha256 =
        Convert.FromHexString("2976aa0d70a830b39c71ef76b61a33dcd25149d1b4c618c5ddf9eb593f079405");

    private static readonly Lazy<RpCryptTables> Embedded = new(LoadEmbedded);

    private readonly byte[] _data;

    private RpCryptTables(byte[] data) => _data = data;

    /// <summary>The tables embedded in this assembly.</summary>
    public static RpCryptTables Default => Embedded.Value;

    internal ReadOnlySpan<byte> HmacKey => _data.AsSpan(0, HmacKeySize);
    internal ReadOnlySpan<byte> RegistKeys0 => _data.AsSpan(HmacKeySize, RegistTableSize);
    internal ReadOnlySpan<byte> RegistKeys1 => _data.AsSpan(HmacKeySize + RegistTableSize, RegistTableSize);
    internal ReadOnlySpan<byte> SessionKeysA => _data.AsSpan(HmacKeySize + (2 * RegistTableSize), SessionTableSize);
    internal ReadOnlySpan<byte> SessionKeysB => _data.AsSpan(HmacKeySize + (2 * RegistTableSize) + SessionTableSize, SessionTableSize);

    public static RpCryptTables FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length != TotalSize || !SHA256.HashData(data).AsSpan().SequenceEqual(ExpectedSha256))
            throw new InvalidDataException("The Remote Play tables are not valid.");
        return new RpCryptTables(data.ToArray());
    }

    private static RpCryptTables LoadEmbedded()
    {
        using var stream = typeof(RpCryptTables).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return FromBytes(buffer.ToArray());
    }
}
