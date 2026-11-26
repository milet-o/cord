using System.Globalization;
using System.Net;
using System.Text;

namespace Cord.Protocol.Discovery;

public static class DiscoveryMessages
{
    private const int StatusReady = 200;
    private const int StatusStandby = 620;

    public static byte[] Search() =>
        Encode(
            "SRCH * HTTP/1.1\n"
            + $"device-discovery-protocol-version:{DiscoveryProtocol.ProtocolVersion}\n");

    public static byte[] Wakeup(ulong credential) =>
        Encode(
            "WAKEUP * HTTP/1.1\n"
            + "client-type:vr\n"
            + "auth-type:R\n"
            + "model:w\n"
            + "app-type:r\n"
            + $"user-credential:{credential.ToString(CultureInfo.InvariantCulture)}\n"
            + $"device-discovery-protocol-version:{DiscoveryProtocol.ProtocolVersion}\n");

    public static bool TryParseResponse(ReadOnlySpan<byte> datagram, IPAddress sender, out DiscoveredConsole? console)
    {
        console = null;

        var nul = datagram.IndexOf((byte)0);
        if (nul >= 0)
            datagram = datagram[..nul];

        var lines = Encoding.ASCII.GetString(datagram).Split('\n');
        if (!TryParseStatusCode(lines[0].TrimEnd('\r'), out var statusCode))
            return false;

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in lines.AsSpan(1))
        {
            var line = rawLine.TrimEnd('\r');
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;
            headers[line[..separator]] = line[(separator + 1)..].Trim();
        }

        console = new DiscoveredConsole(
            sender,
            statusCode switch
            {
                StatusReady => ConsoleState.Ready,
                StatusStandby => ConsoleState.Standby,
                _ => ConsoleState.Unknown,
            },
            headers.GetValueOrDefault("host-id"),
            headers.GetValueOrDefault("host-name"),
            headers.GetValueOrDefault("host-type"),
            headers.GetValueOrDefault("system-version"),
            headers.GetValueOrDefault("device-discovery-protocol-version"),
            int.TryParse(headers.GetValueOrDefault("host-request-port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : null,
            headers.GetValueOrDefault("running-app-titleid"),
            headers.GetValueOrDefault("running-app-name"));
        return true;
    }

    private static bool TryParseStatusCode(string statusLine, out int statusCode)
    {
        statusCode = 0;
        var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            && parts[0].StartsWith("HTTP/", StringComparison.Ordinal)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out statusCode);
    }

    private static byte[] Encode(string message)
    {
        var bytes = new byte[Encoding.ASCII.GetByteCount(message) + 1];
        Encoding.ASCII.GetBytes(message, bytes);
        return bytes;
    }
}
