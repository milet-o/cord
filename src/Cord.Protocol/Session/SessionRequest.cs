using System.Globalization;
using System.Net;
using System.Text;
using Cord.Protocol.Crypto;
using Cord.Protocol.Http;

namespace Cord.Protocol.Session;

/// <summary>Asks a registered console for a new session nonce (docs/protocol/05-session-request.md).</summary>
public static class SessionRequest
{
    public const int DefaultPort = 9295;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<byte[]> RequestNonceAsync(
        IPEndPoint console,
        ReadOnlyMemory<byte> registKey,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        await using var connection = await RpHttpConnection.ConnectAsync(console, timeout.Token);
        await connection.SendAsync(BuildRequest(console, registKey.Span), ReadOnlyMemory<byte>.Empty, timeout.Token);
        var response = await connection.ReadResponseAsync(timeout.Token);

        if (response.StatusCode != 200)
            throw RemotePlayException.FromApplicationReason(response.ApplicationReason, "Session request");

        var nonce = TryDecodeBase64(response.Header("RP-Nonce"));
        if (nonce?.Length != RpCipher.KeySize)
            throw new RemotePlayException(RemotePlayError.InvalidResponse, "Session response has no valid nonce.");
        return nonce;
    }

    internal static string BuildRequest(IPEndPoint console, ReadOnlySpan<byte> registKey) =>
        "GET /sie/ps5/rp/sess/init HTTP/1.1\r\n"
        + $"Host: {console.Address}:{console.Port.ToString(CultureInfo.InvariantCulture)}\r\n"
        + "User-Agent: remoteplay Windows\r\n"
        + "Connection: close\r\n"
        + "Content-Length: 0\r\n"
        + $"RP-Registkey: {RegistKeyHex(registKey)}\r\n"
        + "Rp-Version: 1.0\r\n"
        + "\r\n";

    internal static string RegistKeyHex(ReadOnlySpan<byte> registKey)
    {
        var nul = registKey.IndexOf((byte)0);
        return Convert.ToHexStringLower(nul >= 0 ? registKey[..nul] : registKey);
    }

    private static byte[]? TryDecodeBase64(string? value)
    {
        if (value is null)
            return null;
        var buffer = new byte[Encoding.ASCII.GetByteCount(value)];
        return Convert.TryFromBase64String(value, buffer, out var written) ? buffer[..written] : null;
    }
}
