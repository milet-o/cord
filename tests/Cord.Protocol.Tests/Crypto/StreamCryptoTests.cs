using System.Security.Cryptography;
using Cord.Protocol.Crypto;
using static Cord.Protocol.Tests.Support.TestBytes;

namespace Cord.Protocol.Tests.Crypto;

// GkCrypt expected values were produced by a reference client for the inputs below.
public class StreamCryptoTests
{
    private static readonly byte[] Secret = Sequence(32, i => 0x40 + i);
    private static readonly byte[] HandshakeKey = Sequence(16, i => 0xA0 + i);
    private static readonly byte[] Data = Sequence(40, i => i);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(100)]
    public void GHash_MatchesPlatformGcmTag(int aadLength)
    {
        var key = Sequence(16, i => i * 3);
        var nonce = Sequence(12, i => 0x50 + i);
        var aad = Sequence(aadLength, i => i * 11);

        var expected = new byte[16];
        using (var gcm = new AesGcm(key, 16))
            gcm.Encrypt(nonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, expected, aad);

        using var aes = Aes.Create();
        aes.Key = key;
        var hashKey = aes.EncryptEcb(new byte[16], PaddingMode.None);
        var ghash = new GHash(hashKey);
        ghash.Update(aad);
        ghash.UpdateLengths((ulong)aadLength * 8, 0);
        var s = new byte[16];
        ghash.GetResult(s);
        var j0 = aes.EncryptEcb([.. nonce, 0, 0, 0, 1], PaddingMode.None);

        Assert.Equal(expected, s.Zip(j0, (a, b) => (byte)(a ^ b)).ToArray());
    }

    [Theory]
    [InlineData(GkCrypt.LocalIndex, "8a7f882254156a58bc9d82e452ed2ab3", "26c1d0a42cbc223dfe2c5735de490150", "d469e03c6156e31832688ef0c5770dd8")]
    [InlineData(GkCrypt.RemoteIndex, "2e7e47c3446bd891a5fc2e800e5629b5", "cc029297942539943354c44e8602d212", "5a2a0c5be6198ad3585dc6f3675eeb5b")]
    public void Constructor_DerivesKeys(byte index, string key, string iv, string gmacKey)
    {
        using var crypt = new GkCrypt(index, HandshakeKey, Secret);

        Assert.Equal(Hex(key), crypt.KeyBase);
        Assert.Equal(Hex(iv), crypt.Iv.ToArray());
        Assert.Equal(Hex(gmacKey), crypt.GmacKeyBase.ToArray());
    }

    [Fact]
    public void GenerateKeystream_MatchesReference()
    {
        using var crypt = new GkCrypt(GkCrypt.LocalIndex, HandshakeKey, Secret);
        var keystream = new byte[64];

        crypt.GenerateKeystream(0x30 / 16, keystream);

        Assert.StartsWith(
            "f41b78fde3aa30f3224e242316d4a9a6972a8f3e4cdc49774be49dddc8d6b964aa98f1e843b94692cdebec4cb5c2799e0922bd35e2b731c",
            Convert.ToHexStringLower(keystream));
    }

    [Theory]
    [InlineData(GkCrypt.LocalIndex, "0b999f195b41627a9b76ee074d68757bb31bade369ec4d613a5d5ef2f9eb9d6b4ab41ab62925deeb")]
    [InlineData(GkCrypt.RemoteIndex, "bf83777bb0425cb797de1d4b9a4c8066be794b5c9b6bd36aebbe640186d87ef5189fd3ec0caf3ad6")]
    public void Transform_AtUnalignedPosition(byte index, string expected)
    {
        using var crypt = new GkCrypt(index, HandshakeKey, Secret);
        var buffer = Data.ToArray();

        crypt.Transform(0x1235, buffer);

        Assert.Equal(Hex(expected), buffer);
        crypt.Transform(0x1235, buffer);
        Assert.Equal(Data, buffer);
    }

    [Theory]
    [InlineData(GkCrypt.LocalIndex, 0UL, "736ff3f8")]
    [InlineData(GkCrypt.LocalIndex, 1UL, "736ff3f8")]
    [InlineData(GkCrypt.LocalIndex, 16UL, "be6f45ec")]
    [InlineData(GkCrypt.LocalIndex, 44999UL, "68f658e5")]
    [InlineData(GkCrypt.LocalIndex, 45000UL, "68f658e5")]
    [InlineData(GkCrypt.LocalIndex, 45001UL, "f4c0ebda")]
    [InlineData(GkCrypt.LocalIndex, 90001UL, "cd211293")]
    [InlineData(GkCrypt.RemoteIndex, 0UL, "52b14825")]
    [InlineData(GkCrypt.RemoteIndex, 16UL, "eb1a669f")]
    [InlineData(GkCrypt.RemoteIndex, 45001UL, "b24ca5c1")]
    [InlineData(GkCrypt.RemoteIndex, 90001UL, "6d61020d")]
    public void ComputeGmac_MatchesReference(byte index, ulong keyPos, string expected)
    {
        using var crypt = new GkCrypt(index, HandshakeKey, Secret);
        var gmac = new byte[4];

        crypt.ComputeGmac(keyPos, Data, gmac);

        Assert.Equal(Hex(expected), gmac);
    }

    [Fact]
    public void ComputeGmac_HandlesOlderKeyAfterRotation()
    {
        using var crypt = new GkCrypt(GkCrypt.LocalIndex, HandshakeKey, Secret);
        var gmac = new byte[4];

        crypt.ComputeGmac(90001, Data, gmac);
        crypt.ComputeGmac(16, Data, gmac);

        Assert.Equal(Hex("be6f45ec"), gmac);
    }

    [Fact]
    public void KeyExchange_BothSidesDeriveSameSecret()
    {
        using var client = new StreamKeyExchange();
        using var console = new StreamKeyExchange();

        var clientSecret = client.DeriveSecret(console.PublicKey, StreamKeyExchange.Sign(HandshakeKey, console.PublicKey), HandshakeKey);
        var consoleSecret = console.DeriveSecret(client.PublicKey, StreamKeyExchange.Sign(HandshakeKey, client.PublicKey), HandshakeKey);

        Assert.Equal(StreamKeyExchange.PublicKeySize, client.PublicKey.Length);
        Assert.Equal(StreamKeyExchange.SecretSize, clientSecret.Length);
        Assert.Equal(clientSecret, consoleSecret);
    }

    [Fact]
    public void KeyExchange_RejectsBadSignature()
    {
        using var client = new StreamKeyExchange();
        using var console = new StreamKeyExchange();

        Assert.Throws<CryptographicException>(() => client.DeriveSecret(console.PublicKey, new byte[32], HandshakeKey));
    }
}
