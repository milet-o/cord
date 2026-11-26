using System.Security.Cryptography;

namespace Cord.Protocol.Crypto;

/// <summary>secp256k1 ECDH with HMAC-signed public keys (docs/protocol/08-stream-crypto.md).</summary>
public sealed class StreamKeyExchange : IDisposable
{
    public const int PublicKeySize = 65;
    public const int SignatureSize = 32;
    public const int SecretSize = 32;

    private static readonly ECCurve Secp256k1 = ECCurve.CreateFromValue("1.3.132.0.10");

    private readonly ECDiffieHellman _ecdh;

    public StreamKeyExchange()
    {
        _ecdh = ECDiffieHellman.Create(Secp256k1);
        var parameters = _ecdh.ExportParameters(includePrivateParameters: false);
        PublicKey = [0x04, .. parameters.Q.X!, .. parameters.Q.Y!];
    }

    /// <summary>Uncompressed SEC1 point.</summary>
    public byte[] PublicKey { get; }

    public static byte[] Sign(ReadOnlySpan<byte> handshakeKey, ReadOnlySpan<byte> publicKey) =>
        HMACSHA256.HashData(handshakeKey, publicKey);

    public byte[] DeriveSecret(ReadOnlySpan<byte> remotePublicKey, ReadOnlySpan<byte> remoteSignature, ReadOnlySpan<byte> handshakeKey)
    {
        if (!CryptographicOperations.FixedTimeEquals(Sign(handshakeKey, remotePublicKey), remoteSignature))
            throw new CryptographicException("The console's public key signature is invalid.");
        if (remotePublicKey.Length != PublicKeySize || remotePublicKey[0] != 0x04)
            throw new CryptographicException("Unsupported public key format.");

        using var remote = ECDiffieHellman.Create(new ECParameters
        {
            Curve = Secp256k1,
            Q = new ECPoint
            {
                X = remotePublicKey[1..33].ToArray(),
                Y = remotePublicKey[33..].ToArray(),
            },
        });
        return _ecdh.DeriveRawSecretAgreement(remote.PublicKey);
    }

    public void Dispose() => _ecdh.Dispose();
}
