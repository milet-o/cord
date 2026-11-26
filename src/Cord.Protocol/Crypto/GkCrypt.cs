using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Cord.Protocol.Crypto;

/// <summary>
/// Stream cipher and packet authentication of the Takion stream connection
/// (docs/protocol/08-stream-crypto.md).
/// </summary>
public sealed class GkCrypt : IDisposable
{
    public const int BlockSize = 16;
    public const int GmacSize = 4;
    public const byte LocalIndex = 2;
    public const byte RemoteIndex = 3;

    private const ulong GmacKeyRefreshKeyPos = 45000;
    private const ulong GmacKeyRefreshIvOffset = 44910;

    private readonly byte[] _iv;
    private readonly byte[] _gmacKeyBase;
    private readonly Aes _streamAes;
    private readonly Lock _gmacLock = new();
    private ulong _gmacKeyIndex;
    private Aes _gmacAes;
    private GHash _gmacHash;

    public GkCrypt(byte index, ReadOnlySpan<byte> handshakeKey, ReadOnlySpan<byte> ecdhSecret)
    {
        Span<byte> data = stackalloc byte[3 + handshakeKey.Length + 2];
        data[0] = 1;
        data[1] = index;
        data[2] = 0;
        handshakeKey.CopyTo(data[3..]);
        data[^2] = 1;
        data[^1] = 0;
        var derived = HMACSHA256.HashData(ecdhSecret, data);

        KeyBase = derived[..BlockSize];
        _iv = derived[BlockSize..];
        _streamAes = Aes.Create();
        _streamAes.Key = KeyBase;

        _gmacKeyBase = DeriveGmacKey(KeyBase, _iv, 0);
        (_gmacAes, _gmacHash) = CreateGmac(_gmacKeyBase);
    }

    internal byte[] KeyBase { get; }
    internal ReadOnlySpan<byte> Iv => _iv;
    internal ReadOnlySpan<byte> GmacKeyBase => _gmacKeyBase;

    /// <summary>XORs <paramref name="data"/> with the keystream starting at <paramref name="keyPos"/>.</summary>
    public void Transform(ulong keyPos, Span<byte> data)
    {
        if (data.IsEmpty)
            return;

        var skip = (int)(keyPos % BlockSize);
        var blocks = (skip + data.Length + BlockSize - 1) / BlockSize;
        var keystream = blocks <= 128 ? stackalloc byte[blocks * BlockSize] : new byte[blocks * BlockSize];
        GenerateKeystream(keyPos / BlockSize, keystream);

        var source = keystream.Slice(skip, data.Length);
        for (var i = 0; i < data.Length; i++)
            data[i] ^= source[i];
    }

    /// <summary>Computes the 4-byte GMAC of a whole datagram at <paramref name="keyPos"/>.</summary>
    public void ComputeGmac(ulong keyPos, ReadOnlySpan<byte> datagram, Span<byte> gmac)
    {
        Span<byte> iv = stackalloc byte[BlockSize];
        CounterAdd(_iv, keyPos / BlockSize, iv);

        var keyIndex = (keyPos > 0 ? keyPos - 1 : 0) / GmacKeyRefreshKeyPos;
        lock (_gmacLock)
        {
            if (keyIndex == _gmacKeyIndex)
            {
                ComputeGmac(_gmacAes, _gmacHash, iv, datagram, gmac);
                return;
            }

            var (aes, hash) = CreateGmac(keyIndex == 0 ? _gmacKeyBase : DeriveGmacKey(_gmacKeyBase, _iv, keyIndex));
            if (keyIndex > _gmacKeyIndex)
            {
                _gmacAes.Dispose();
                (_gmacAes, _gmacHash, _gmacKeyIndex) = (aes, hash, keyIndex);
                ComputeGmac(_gmacAes, _gmacHash, iv, datagram, gmac);
                return;
            }

            using (aes)
                ComputeGmac(aes, hash, iv, datagram, gmac);
        }
    }

    public void Dispose()
    {
        _streamAes.Dispose();
        _gmacAes.Dispose();
    }

    internal void GenerateKeystream(ulong firstBlock, Span<byte> output)
    {
        for (var i = 0; i < output.Length / BlockSize; i++)
            CounterAdd(_iv, firstBlock + (ulong)i, output.Slice(i * BlockSize, BlockSize));
        _streamAes.EncryptEcb(output, output, PaddingMode.None);
    }

    // Treats the base as a little-endian 128-bit integer.
    private static void CounterAdd(ReadOnlySpan<byte> value, ulong addend, Span<byte> output)
    {
        var low = BinaryPrimitives.ReadUInt64LittleEndian(value);
        var high = BinaryPrimitives.ReadUInt64LittleEndian(value[8..]);
        var sum = low + addend;
        if (sum < low)
            high++;
        BinaryPrimitives.WriteUInt64LittleEndian(output, sum);
        BinaryPrimitives.WriteUInt64LittleEndian(output[8..], high);
    }

    private static byte[] DeriveGmacKey(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ulong index)
    {
        Span<byte> data = stackalloc byte[2 * BlockSize];
        key.CopyTo(data);
        CounterAdd(iv, index * GmacKeyRefreshIvOffset, data[BlockSize..]);
        var digest = SHA256.HashData(data);
        for (var i = 0; i < BlockSize; i++)
            digest[i] ^= digest[BlockSize + i];
        return digest[..BlockSize];
    }

    private static (Aes Aes, GHash Hash) CreateGmac(byte[] key)
    {
        var aes = Aes.Create();
        aes.Key = key;
        Span<byte> hashKey = stackalloc byte[BlockSize];
        aes.EncryptEcb(new byte[BlockSize], hashKey, PaddingMode.None);
        return (aes, new GHash(hashKey));
    }

    // GCM with an empty plaintext, the datagram as additional data and a 16-byte IV.
    private static void ComputeGmac(Aes aes, GHash hash, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> aad, Span<byte> gmac)
    {
        Span<byte> counter = stackalloc byte[BlockSize];
        hash.Reset();
        hash.Update(iv);
        hash.UpdateLengths(0, (ulong)iv.Length * 8);
        hash.GetResult(counter);

        Span<byte> mask = stackalloc byte[BlockSize];
        aes.EncryptEcb(counter, mask, PaddingMode.None);

        Span<byte> tag = stackalloc byte[BlockSize];
        hash.Reset();
        hash.Update(aad);
        hash.UpdateLengths((ulong)aad.Length * 8, 0);
        hash.GetResult(tag);

        for (var i = 0; i < GmacSize; i++)
            gmac[i] = (byte)(tag[i] ^ mask[i]);
    }
}
