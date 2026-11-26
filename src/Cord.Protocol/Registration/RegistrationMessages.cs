using System.Globalization;
using System.Text;
using Cord.Protocol.Crypto;
using Cord.Protocol.Http;

namespace Cord.Protocol.Registration;

public static class RegistrationMessages
{
    public const string ClientType = "dabfa2ec873de5839bee8d3f4c0239c4282c07c25c6077a2931afcf0adc0d34f";

    private const int InnerHeaderOffset = 0x1E0;
    private const byte Filler = (byte)'A';
    private const int Key0OffsetPosition = 0x18D;
    private const int AeropauseHighPosition = 0xC7;
    private const int AeropauseLowPosition = 0x191;

    /// <summary>Builds the binary registration payload and the cipher that protects the exchange.</summary>
    public static (byte[] Payload, RpCipher Cipher) BuildPayload(
        RpCryptTables tables,
        ReadOnlySpan<byte> ambassador,
        PsnAccountId accountId,
        uint pin)
    {
        var inner = Encoding.ASCII.GetBytes(
            $"Client-Type: {ClientType}\r\nNp-AccountId: {accountId.ToBase64()}\r\n");

        var payload = new byte[InnerHeaderOffset + inner.Length];
        payload.AsSpan(0, InnerHeaderOffset).Fill(Filler);

        var key0Offset = payload[Key0OffsetPosition] & 0x1F;
        var key1Offset = payload[0] >> 3;

        var cipher = RpCipher.ForRegistration(tables, ambassador, key0Offset, pin);
        var aeropause = RpCipher.Aeropause(tables, ambassador, key1Offset);
        aeropause.AsSpan(8, 8).CopyTo(payload.AsSpan(AeropauseHighPosition));
        aeropause.AsSpan(0, 8).CopyTo(payload.AsSpan(AeropauseLowPosition));

        cipher.Encrypt(0, inner).CopyTo(payload.AsSpan(InnerHeaderOffset));
        return (payload, cipher);
    }

    public static string RequestHead(int payloadLength) =>
        "POST /sie/ps5/rp/sess/rgst HTTP/1.1\r\n HTTP/1.1\r\n"
        + "HOST: 10.0.2.15\r\n"
        + "User-Agent: remoteplay Windows\r\n"
        + "Connection: close\r\n"
        + $"Content-Length: {payloadLength.ToString(CultureInfo.InvariantCulture)}\r\n"
        + "RP-Version: 1.0\r\n"
        + "\r\n";

    public static RegisteredConsole ParseResponseBody(ReadOnlySpan<byte> decryptedBody)
    {
        var fields = RpHttpResponse.ParseHeaderLines(Encoding.ASCII.GetString(decryptedBody));

        var registKeyText = ParseHex(fields, "PS5-RegistKey");
        var rpKey = ParseHex(fields, "RP-Key");
        var mac = ParseHex(fields, "PS5-Mac");
        if (registKeyText is null || registKeyText.Length > RegisteredConsole.RegistKeySize
            || rpKey?.Length != RegisteredConsole.RpKeySize
            || mac?.Length != RegisteredConsole.MacAddressSize)
        {
            throw new RemotePlayException(RemotePlayError.InvalidResponse, "Registration response is missing required keys.");
        }

        var registKey = new byte[RegisteredConsole.RegistKeySize];
        registKeyText.CopyTo(registKey, 0);

        _ = int.TryParse(fields.GetValueOrDefault("RP-KeyType"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var keyType);
        return new RegisteredConsole(registKey, rpKey, keyType, mac, fields.GetValueOrDefault("PS5-Nickname"));
    }

    private static byte[]? ParseHex(Dictionary<string, string> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value))
            return null;
        try
        {
            return Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
