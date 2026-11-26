using System.Buffers.Binary;

namespace Cord.Protocol.Crypto;

/// <summary>GHASH over GF(2^128) as defined by GCM, using a 4-bit multiplication table.</summary>
internal sealed class GHash
{
    private static readonly ulong[] Reduction =
    [
        0x0000, 0x1C20, 0x3840, 0x2460, 0x7080, 0x6CA0, 0x48C0, 0x54E0,
        0xE100, 0xFD20, 0xD940, 0xC560, 0x9180, 0x8DA0, 0xA9C0, 0xB5E0,
    ];

    private readonly ulong[] _tableHigh = new ulong[16];
    private readonly ulong[] _tableLow = new ulong[16];
    private ulong _stateHigh;
    private ulong _stateLow;

    public GHash(ReadOnlySpan<byte> hashKey)
    {
        var high = BinaryPrimitives.ReadUInt64BigEndian(hashKey);
        var low = BinaryPrimitives.ReadUInt64BigEndian(hashKey[8..]);

        _tableHigh[8] = high;
        _tableLow[8] = low;
        for (var i = 4; i > 0; i >>= 1)
        {
            var carry = (low & 1) * 0xE100000000000000UL;
            low = (high << 63) | (low >> 1);
            high = (high >> 1) ^ carry;
            _tableHigh[i] = high;
            _tableLow[i] = low;
        }

        for (var i = 2; i < 16; i <<= 1)
        {
            for (var j = 1; j < i; j++)
            {
                _tableHigh[i + j] = _tableHigh[i] ^ _tableHigh[j];
                _tableLow[i + j] = _tableLow[i] ^ _tableLow[j];
            }
        }
    }

    public void Reset() => (_stateHigh, _stateLow) = (0, 0);

    /// <summary>Absorbs data, zero padding the final partial block.</summary>
    public void Update(ReadOnlySpan<byte> data)
    {
        Span<byte> block = stackalloc byte[16];
        while (!data.IsEmpty)
        {
            var length = Math.Min(16, data.Length);
            block.Clear();
            data[..length].CopyTo(block);
            _stateHigh ^= BinaryPrimitives.ReadUInt64BigEndian(block);
            _stateLow ^= BinaryPrimitives.ReadUInt64BigEndian(block[8..]);
            Multiply();
            data = data[length..];
        }
    }

    public void UpdateLengths(ulong aadBits, ulong ciphertextBits)
    {
        _stateHigh ^= aadBits;
        _stateLow ^= ciphertextBits;
        Multiply();
    }

    public void GetResult(Span<byte> output)
    {
        BinaryPrimitives.WriteUInt64BigEndian(output, _stateHigh);
        BinaryPrimitives.WriteUInt64BigEndian(output[8..], _stateLow);
    }

    private void Multiply()
    {
        Span<byte> x = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(x, _stateHigh);
        BinaryPrimitives.WriteUInt64BigEndian(x[8..], _stateLow);

        var nibble = x[15] & 0x0F;
        var high = _tableHigh[nibble];
        var low = _tableLow[nibble];

        for (var i = 15; i >= 0; i--)
        {
            var lo = x[i] & 0x0F;
            var hi = x[i] >> 4;

            if (i != 15)
            {
                var remainder = low & 0x0F;
                low = (high << 60) | (low >> 4);
                high = (high >> 4) ^ (Reduction[remainder] << 48);
                high ^= _tableHigh[lo];
                low ^= _tableLow[lo];
            }

            var rem = low & 0x0F;
            low = (high << 60) | (low >> 4);
            high = (high >> 4) ^ (Reduction[rem] << 48);
            high ^= _tableHigh[hi];
            low ^= _tableLow[hi];
        }

        _stateHigh = high;
        _stateLow = low;
    }
}
