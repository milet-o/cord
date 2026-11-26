using Cord.Protocol.Crypto;
using Cord.Protocol.Tests.Support;
using static Cord.Protocol.Tests.Support.TestBytes;

namespace Cord.Protocol.Tests.Crypto;

// Expected values were produced by a reference client for the inputs below.
public class RpCipherTests
{
    private const uint Pin = 12345678;
    private static readonly byte[] Ambassador = Sequence(16, i => (i * 7) + 1);
    private static readonly byte[] Nonce = Sequence(16, i => 0xF0 - (i * 5));
    private static readonly byte[] Morning = Sequence(16, i => (i * 13) + 3);
    private static readonly byte[] Plaintext = Sequence(40, i => i);

    [Fact]
    public void ForRegistration_DerivesBright()
    {
        using var cipher = RpCipher.ForRegistration(TestTables.Value, Ambassador, keyOffset: 1, Pin);

        Assert.Equal(Hex("d860e146bdb0bd94b460070bb1f3866d"), cipher.Bright.ToArray());
    }

    [Fact]
    public void Aeropause_ObfuscatesAmbassador()
    {
        Assert.Equal(Hex("7c13437911b0053636df5ac733a9cad1"), RpCipher.Aeropause(TestTables.Value, Ambassador, keyOffset: 8));
    }

    [Fact]
    public void ComputeIv_UsesCounter()
    {
        using var cipher = RpCipher.ForRegistration(TestTables.Value, Ambassador, 1, Pin);

        Assert.Equal(Hex("b81aec476785cb5cd6daf39a5a232c9e"), cipher.ComputeIv(0));
        Assert.Equal(Hex("f5f47e9bb13f40bbad064eddb2731569"), cipher.ComputeIv(0x0102030405060708));
    }

    [Fact]
    public void Encrypt_MatchesReference()
    {
        using var cipher = RpCipher.ForRegistration(TestTables.Value, Ambassador, 1, Pin);

        Assert.Equal(Hex("1bc81d92ab7e15b3ed0adc496bc6896ea9e61eb648b90e3c2b66458d47a4a0e2f2165e1b94af169c"), cipher.Encrypt(0, Plaintext));
        Assert.Equal(Hex("340e607eff574afac7779825a4f54865bb538d9e297f738adf84bb9596fb85c5a8cda153532b0ead"), cipher.Encrypt(5, Plaintext));
    }

    [Fact]
    public void Decrypt_InvertsEncrypt()
    {
        using var cipher = RpCipher.ForRegistration(TestTables.Value, Ambassador, 1, Pin);

        Assert.Equal(Plaintext, cipher.Decrypt(9, cipher.Encrypt(9, Plaintext)));
    }

    [Fact]
    public void ForSession_DerivesKeysFromNonceAndMorning()
    {
        using var cipher = RpCipher.ForSession(TestTables.Value, Nonce, Morning);

        Assert.Equal(Hex("40929921febbd5e5d2ccbb8b26a48d04"), cipher.Bright.ToArray());
        Assert.Equal(Hex("d468609e35f319480b11a323f9f1c287"), cipher.Ambassador.ToArray());
        Assert.Equal(Hex("7bf7d37c0bc4d6ebb291e1a20d821666bd46780ac459d85326d749f6255e8c917329a7074fe56179"), cipher.Encrypt(3, Plaintext));
    }

    [Fact]
    public void FromBytes_RejectsWrongContent()
    {
        Assert.Throws<InvalidDataException>(() => RpCryptTables.FromBytes(new byte[8208]));
    }
}
