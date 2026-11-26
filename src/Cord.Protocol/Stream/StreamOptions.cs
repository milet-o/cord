using Cord.Protocol.Session;

namespace Cord.Protocol.Stream;

/// <summary>Requested stream quality (docs/protocol/09-stream-connection.md#launch-spec).</summary>
public sealed record StreamOptions(
    int Width,
    int Height,
    int MaxFps,
    int BitrateKbps,
    VideoCodec Codec,
    bool DualSense = true,
    double MaxReportedPacketLoss = 0.05,
    bool AudioOnly = false)
{
    public bool IsHevc => Codec is VideoCodec.Hevc or VideoCodec.HevcHdr;

    public bool IsHdr => Codec == VideoCodec.HevcHdr;

    public static StreamOptions ForAudioAndController(bool dualSense) =>
        new(640, 360, 30, 1000, VideoCodec.H264, dualSense, AudioOnly: true);
}
