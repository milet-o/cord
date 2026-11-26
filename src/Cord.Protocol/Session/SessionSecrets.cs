using System.Security.Cryptography;

namespace Cord.Protocol.Session;

/// <summary>Per-session random values (docs/protocol/05-session-request.md).</summary>
public sealed class SessionSecrets
{
    public const int HandshakeKeySize = 16;
    public const int DeviceIdSize = 32;

    private static readonly byte[] DeviceIdPrefix = [0x00, 0x18, 0x00, 0x00, 0x00, 0x07, 0x00, 0x40, 0x00, 0x80];
    private const int DeviceIdSuffixSize = 6;

    public SessionSecrets(byte[] handshakeKey, byte[] deviceId)
    {
        if (handshakeKey.Length != HandshakeKeySize)
            throw new ArgumentException($"Handshake key must be {HandshakeKeySize} bytes.", nameof(handshakeKey));
        if (deviceId.Length != DeviceIdSize)
            throw new ArgumentException($"Device id must be {DeviceIdSize} bytes.", nameof(deviceId));
        HandshakeKey = handshakeKey;
        DeviceId = deviceId;
    }

    public byte[] HandshakeKey { get; }

    public byte[] DeviceId { get; }

    public static SessionSecrets Generate()
    {
        var deviceId = new byte[DeviceIdSize];
        DeviceIdPrefix.CopyTo(deviceId, 0);
        RandomNumberGenerator.Fill(deviceId.AsSpan(DeviceIdPrefix.Length, DeviceIdSize - DeviceIdPrefix.Length - DeviceIdSuffixSize));
        return new SessionSecrets(RandomNumberGenerator.GetBytes(HandshakeKeySize), deviceId);
    }
}
