using System.Text.Json.Serialization;
using Cord.Protocol.Session;
using Cord.Protocol.Stream;

namespace Cord.Core.Settings;

/// <summary>
/// Stream resolutions. A PS5 rejects anything above 1080p at stream start
/// (docs/protocol/09-stream-connection.md#launch-spec).
/// </summary>
public enum Resolution
{
    P360,
    P540,
    P720,
    P1080,
}

public enum FrameGenMode
{
    Off,
    LowLatencyExtrapolation,
    SmoothInterpolation,
}

public sealed record StreamQuality(
    [property: JsonConverter(typeof(ResolutionJsonConverter))] Resolution Resolution = Resolution.P1080,
    int Fps = 60,
    VideoCodec Codec = VideoCodec.Hevc,
    int? BitrateKbps = null)
{
    public (int Width, int Height) Size => Resolution switch
    {
        Resolution.P360 => (640, 360),
        Resolution.P540 => (960, 540),
        Resolution.P720 => (1280, 720),
        Resolution.P1080 => (1920, 1080),
        _ => throw new ArgumentOutOfRangeException(nameof(Resolution)),
    };

    /// <summary>Default bitrate per resolution when none is set, scaled for 60 fps.</summary>
    public int EffectiveBitrateKbps => BitrateKbps ?? Resolution switch
    {
        Resolution.P360 => 2000,
        Resolution.P540 => 6000,
        Resolution.P720 => 10000,
        Resolution.P1080 => 15000,
        _ => 15000,
    };

    public StreamOptions ToStreamOptions(bool dualSense, double maxReportedLoss)
    {
        var (width, height) = Size;
        return new StreamOptions(width, height, Fps, EffectiveBitrateKbps, Codec, dualSense, maxReportedLoss);
    }
}

public sealed record WebShareSettings
{
    public bool RequireApproval { get; init; } = true;

    public bool RequireDailyPassword { get; init; }

    public bool LockPsButton { get; init; } = true;

    public bool LockToCurrentGame { get; init; } = true;

    public bool AllowRemoteRestMode { get; init; } = true;

    public int MaxClients { get; init; } = 3;

    public int DailyPasswordEpoch { get; init; }

    public string? AdminKeyProtected { get; init; }

    public string? PsButtonUnlockPasswordProtected { get; init; }

    public string? TelegramBotTokenProtected { get; init; }

    public string? TelegramChatId { get; init; }

    public string? CloudflareTunnelTokenProtected { get; init; }

    public string? CloudflarePublicUrl { get; init; }
}

public sealed record AppSettings
{
    public StreamQuality Quality { get; init; } = new();

    /// <summary>Set once the first-run setup (sign in, pair, pick a quality) was finished or skipped.</summary>
    public bool SetupCompleted { get; init; }

    /// <summary>Set once the first-run interactive guide has been viewed or skipped.</summary>
    public bool GuideCompleted { get; init; }

    /// <summary>Optional release version the user chose to skip in the update popup.</summary>
    public string? SkippedUpdateVersion { get; init; }

    public bool HardwareDecoding { get; init; } = true;

    public bool VSync { get; init; } = true;

    public bool LowLatencyMode { get; init; } = true;

    public FrameGenMode FrameGen { get; init; } = FrameGenMode.Off;

    public float Sharpness { get; init; } = 0.35f;

    public bool Fullscreen { get; init; }

    /// <summary>Upper bound of the loss ratio reported to the console (docs/protocol/14-congestion.md).</summary>
    public double MaxReportedPacketLoss { get; init; } = 0.05;

    public string? FfmpegPath { get; init; }

    public string? PsnClientId { get; init; }

    public string? PsnClientSecret { get; init; }

    public float AudioVolume { get; init; } = 1.0f;

    public bool ShowMiniHud { get; init; } = true;

    public bool MinimizeToTray { get; init; } = true;

    public bool LightTheme { get; init; }

    public bool MouseRightStick { get; init; }

    public float MouseSensitivity { get; init; } = 1.0f;

    public Dictionary<string, string>? KeyBindings { get; init; }

    public WebShareSettings WebShare { get; init; } = new();
}
