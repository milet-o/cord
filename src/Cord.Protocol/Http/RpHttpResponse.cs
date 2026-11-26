using System.Globalization;
using System.Text;

namespace Cord.Protocol.Http;

/// <summary>
/// The HTTP-like responses used by registration, session request and ctrl. They are parsed by
/// hand because the console does not speak compliant HTTP.
/// </summary>
public sealed class RpHttpResponse
{
    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    private readonly Dictionary<string, string> _headers;

    private RpHttpResponse(int statusCode, Dictionary<string, string> headers)
    {
        StatusCode = statusCode;
        _headers = headers;
    }

    public int StatusCode { get; }

    public IReadOnlyDictionary<string, string> Headers => _headers;

    public string? Header(string name) => _headers.GetValueOrDefault(name);

    public uint? ApplicationReason =>
        uint.TryParse(Header("RP-Application-Reason"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var reason)
            ? reason
            : null;

    public static int FindHeaderEnd(ReadOnlySpan<byte> buffer)
    {
        var index = buffer.IndexOf(HeaderTerminator);
        return index < 0 ? -1 : index + HeaderTerminator.Length;
    }

    public static RpHttpResponse Parse(ReadOnlySpan<byte> headerBlock)
    {
        var lines = Encoding.ASCII.GetString(headerBlock).Split("\r\n");
        var status = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (status.Length < 2
            || !status[0].StartsWith("HTTP/", StringComparison.Ordinal)
            || !int.TryParse(status[1], NumberStyles.None, CultureInfo.InvariantCulture, out var statusCode))
        {
            throw new InvalidDataException("Invalid response status line.");
        }

        return new RpHttpResponse(statusCode, ParseHeaders(lines.AsSpan(1)));
    }

    /// <summary>Parses "Key: Value" lines, as found in decrypted registration responses.</summary>
    public static Dictionary<string, string> ParseHeaderLines(string text) => ParseHeaders(text.Split("\r\n"));

    private static Dictionary<string, string> ParseHeaders(ReadOnlySpan<string> lines)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var separator = line.IndexOf(':');
            if (separator > 0)
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return headers;
    }
}
