using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Cord.Protocol.Crypto;

/// <summary>
/// AES-128-CFB128 cipher used for registration, the ctrl channel and the launch spec
/// (docs/protocol/03-rpcrypt.md).
/// </summary>
public sealed class RpCipher : IDisposable
{
    public const int KeySize = 16;

    private const int TableStride = 0x20;
    private const int SessionTableEntrySize = 0x70;

    private readonly byte[] _ambassador;
    private readonly byte[] _hmacKey;
    private readonly Aes _aes;

    private RpCipher(ReadOnlySpan<byte> bright, ReadOnlySpan<byte> ambassador, ReadOnlySpan<byte> hmacKey)
    {
        _ambassador = ambassador.ToArray();
        _hmacKey = hmacKey.ToArray();
        _aes = Aes.Create();
        _aes.Key = bright.ToArray();
    }

    internal ReadOnlySpan<byte> Bright => _aes.Key;
    internal ReadOnlySpan<byte> Ambassador => _ambassador;

    public static RpCipher ForRegistration(RpCryptTables tables, ReadOnlySpan<byte> ambassador, int keyOffset, uint pin)
    {
        RequireKeySize(ambassador, nameof(ambassador));
        ArgumentOutOfRangeException.ThrowIfNegative(keyOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(keyOffset, TableStride);

        Span<byte> bright = stackalloc byte[KeySize];
        for (var i = 0; i < KeySize; i++)
            bright[i] = tables.RegistKeys0[(i * TableStride) + keyOffset];

        Span<byte> pinBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(pinBytes, pin);
        for (var i = 0; i < pinBytes.Length; i++)
            bright[12 + i] ^= pinBytes[i];

        return new RpCipher(bright, ambassador, tables.HmacKey);
    }

    public static RpCipher ForSession(RpCryptTables tables, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> morning)
    {
        RequireKeySize(nonce, nameof(nonce));
        RequireKeySize(morning, nameof(morning));

        var keyA = tables.SessionKeysA.Slice((nonce[0] >> 3) * SessionTableEntrySize, KeySize);
        var keyB = tables.SessionKeysB.Slice((nonce[7] >> 3) * SessionTableEntrySize, KeySize);

        Span<byte> ambassador = stackalloc byte[KeySize];
        Span<byte> bright = stackalloc byte[KeySize];
        for (var i = 0; i < KeySize; i++)
        {
            ambassador[i] = (byte)((byte)(nonce[i] - 0x2D - i) ^ keyA[i]);
            bright[i] = (byte)((byte)(morning[i] + 0x18 + i) ^ nonce[i] ^ keyB[i]);
        }

        return new RpCipher(bright, ambassador, tables.HmacKey);
    }

    public static byte[] Aeropause(RpCryptTables tables, ReadOnlySpan<byte> ambassador, int keyOffset)
    {
        RequireKeySize(ambassador, nameof(ambassador));
        ArgumentOutOfRangeException.ThrowIfNegative(keyOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(keyOffset, TableStride);

        var aeropause = new byte[KeySize];
        for (var i = 0; i < KeySize; i++)
            aeropause[i] = (byte)((ambassador[i] ^ tables.RegistKeys1[(i * TableStride) + keyOffset]) + 0xD3 + i);
        return aeropause;
    }

    public byte[] Encrypt(ulong counter, ReadOnlySpan<byte> plaintext) => Transform(counter, plaintext, encrypt: true);

    public byte[] Decrypt(ulong counter, ReadOnlySpan<byte> ciphertext) => Transform(counter, ciphertext, encrypt: false);

    public void Dispose() => _aes.Dispose();

    // CFB-128 built on ECB because the platform CFB only accepts whole blocks.
    private byte[] Transform(ulong counter, ReadOnlySpan<byte> input, bool encrypt)
    {
        var output = new byte[input.Length];
        Span<byte> feedback = stackalloc byte[KeySize];
        Span<byte> keystream = stackalloc byte[KeySize];
        ComputeIv(counter).CopyTo(feedback);

        for (var offset = 0; offset < input.Length; offset += KeySize)
        {
            _aes.EncryptEcb(feedback, keystream, PaddingMode.None);
            var length = Math.Min(KeySize, input.Length - offset);
            var inBlock = input.Slice(offset, length);
            var outBlock = output.AsSpan(offset, length);
            for (var i = 0; i < length; i++)
                outBlock[i] = (byte)(inBlock[i] ^ keystream[i]);
            (encrypt ? outBlock : inBlock).CopyTo(feedback);
        }

        return output;
    }

    internal byte[] ComputeIv(ulong counter)
    {
        Span<byte> input = stackalloc byte[KeySize + sizeof(ulong)];
        _ambassador.CopyTo(input);
        BinaryPrimitives.WriteUInt64BigEndian(input[KeySize..], counter);
        return HMACSHA256.HashData(_hmacKey, input)[..KeySize];
    }

    private static void RequireKeySize(ReadOnlySpan<byte> value, string name)
    {
        if (value.Length != KeySize)
            throw new ArgumentException($"Expected {KeySize} bytes.", name);
    }
}
