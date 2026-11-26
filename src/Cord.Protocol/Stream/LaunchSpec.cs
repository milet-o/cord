using System.Text;
using System.Text.Json;
using Cord.Protocol.Crypto;

namespace Cord.Protocol.Stream;

/// <summary>The encrypted JSON stream request carried in BIG (docs/protocol/09-stream-connection.md#launch-spec).</summary>
public static class LaunchSpec
{
    public static string BuildJson(StreamOptions options, int mtu, int rttMs, ReadOnlySpan<byte> handshakeKey)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("sessionId", "sessionId4321");

            json.WriteStartArray("streamResolutions");
            json.WriteStartObject();
            json.WriteStartObject("resolution");
            json.WriteNumber("width", options.Width);
            json.WriteNumber("height", options.Height);
            json.WriteEndObject();
            json.WriteNumber("maxFps", options.MaxFps);
            json.WriteNumber("score", 10);
            json.WriteEndObject();
            json.WriteEndArray();

            json.WriteStartObject("network");
            json.WriteNumber("bwKbpsSent", options.BitrateKbps);
            json.WriteNumber("bwLoss", 0.001);
            json.WriteNumber("mtu", mtu);
            json.WriteNumber("rtt", rttMs);
            json.WriteStartArray("ports");
            json.WriteNumberValue(53);
            json.WriteNumberValue(2053);
            json.WriteEndArray();
            json.WriteEndObject();

            json.WriteNumber("slotId", 1);

            json.WriteStartObject("appSpecification");
            json.WriteNumber("minFps", 30);
            json.WriteNumber("minBandwidth", 0);
            json.WriteString("extTitleId", "ps3");
            json.WriteNumber("version", 1);
            json.WriteNumber("timeLimit", 1);
            json.WriteNumber("startTimeout", 100);
            json.WriteNumber("afkTimeout", 100);
            json.WriteNumber("afkTimeoutDisconnect", 100);
            json.WriteEndObject();

            json.WriteStartObject("konan");
            json.WriteString("ps3AccessToken", "accessToken");
            json.WriteString("ps3RefreshToken", "refreshToken");
            json.WriteEndObject();

            json.WriteStartObject("requestGameSpecification");
            json.WriteString("model", "bravia_tv");
            json.WriteString("platform", "android");
            json.WriteString("audioChannels", "5.1");
            json.WriteString("language", "sp");
            json.WriteString("acceptButton", "X");
            json.WriteStartArray("connectedControllers");
            json.WriteStringValue("xinput");
            json.WriteStringValue("ds3");
            json.WriteStringValue("ds4");
            json.WriteEndArray();
            json.WriteString("yuvCoefficient", "bt601");
            json.WriteString("videoEncoderProfile", "hw4.1");
            json.WriteString("audioEncoderProfile", "audio1");
            json.WriteString("adaptiveStreamMode", "resize");
            json.WriteEndObject();

            json.WriteStartObject("userProfile");
            json.WriteString("onlineId", "psnId");
            json.WriteString("npId", "npId");
            json.WriteString("region", "US");
            json.WriteStartArray("languagesUsed");
            json.WriteStringValue("en");
            json.WriteStringValue("jp");
            json.WriteEndArray();
            json.WriteEndObject();

            json.WriteString("videoCodec", options.IsHevc ? "hevc" : "avc");
            json.WriteString("dynamicRange", options.IsHdr ? "HDR" : "SDR");
            json.WriteString("handshakeKey", Convert.ToBase64String(handshakeKey));
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// XORs the JSON (plus a trailing NUL) with the RP cipher output for zeros at counter 0,
    /// then base64-encodes it.
    /// </summary>
    public static string Encrypt(string json, RpCipher cipher)
    {
        var plain = Encoding.UTF8.GetBytes(json + "\0");
        var mask = cipher.Encrypt(0, new byte[plain.Length]);
        for (var i = 0; i < plain.Length; i++)
            plain[i] ^= mask[i];
        return Convert.ToBase64String(plain);
    }
}
