using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Cord.WebShare;

/// <summary>
/// Extracts the real visitor IP forwarded by Cloudflare and resolves an approximate
/// city/region/ISP label for owner security notifications.
/// </summary>
public sealed class IpGeoResolver : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public static string ExtractClientIp(IReadOnlyDictionary<string, string> headers, IPAddress? remoteAddress)
    {
        if (headers.TryGetValue("CF-Connecting-IP", out var cfIp) && !string.IsNullOrWhiteSpace(cfIp))
            return cfIp.Trim();

        if (headers.TryGetValue("X-Forwarded-For", out var forwarded) && !string.IsNullOrWhiteSpace(forwarded))
        {
            var comma = forwarded.IndexOf(',', StringComparison.Ordinal);
            var first = comma >= 0 ? forwarded[..comma] : forwarded;
            if (!string.IsNullOrWhiteSpace(first))
                return first.Trim();
        }

        if (remoteAddress is not null)
        {
            return remoteAddress.IsIPv4MappedToIPv6
                ? remoteAddress.MapToIPv4().ToString()
                : remoteAddress.ToString();
        }

        return "desconhecido";
    }

    public static bool IsPrivateOrLoopback(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr))
            return true;

        if (addr.IsIPv4MappedToIPv6)
            addr = addr.MapToIPv4();

        if (IPAddress.IsLoopback(addr))
            return true;

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = addr.GetAddressBytes();
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }

        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
            return addr.IsIPv6LinkLocal || addr.IsIPv6SiteLocal || addr.IsIPv6UniqueLocal;

        return false;
    }

    public async Task<string> ResolveLocationAsync(string ip, string? countryHint, CancellationToken cancellationToken)
    {
        if (IsPrivateOrLoopback(ip))
            return "Rede local";

        if (_cache.TryGetValue(ip, out var cached))
            return cached;

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await _http.GetAsync(
                new Uri($"http://ip-api.com/json/{Uri.EscapeDataString(ip)}?fields=status,countryCode,regionName,city,isp"),
                linked.Token);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: linked.Token);
                var root = doc.RootElement;
                if (root.TryGetProperty("status", out var status) && string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase))
                {
                    var city = root.TryGetProperty("city", out var c) ? c.GetString() : null;
                    var region = root.TryGetProperty("regionName", out var r) ? r.GetString() : null;
                    var country = root.TryGetProperty("countryCode", out var cc) ? cc.GetString() : null;
                    var isp = root.TryGetProperty("isp", out var i) ? i.GetString() : null;

                    var parts = new List<string>(3);
                    if (!string.IsNullOrWhiteSpace(city))
                        parts.Add(city);
                    if (!string.IsNullOrWhiteSpace(region))
                        parts.Add(region);
                    var place = parts.Count > 0 ? string.Join(", ", parts) : "";
                    if (!string.IsNullOrWhiteSpace(country))
                        place = place.Length > 0 ? $"{place} ({country})" : country;
                    if (!string.IsNullOrWhiteSpace(isp))
                        place = place.Length > 0 ? $"{place} · {isp}" : isp;

                    if (place.Length > 0)
                    {
                        _cache[ip] = place;
                        return place;
                    }
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
        }

        var fallback = !string.IsNullOrWhiteSpace(countryHint) ? $"País {countryHint}" : "Localização indisponível";
        _cache[ip] = fallback;
        return fallback;
    }

    public void Dispose() => _http.Dispose();
}
