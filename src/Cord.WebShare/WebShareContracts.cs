using Cord.Protocol.Input;

namespace Cord.WebShare;

public enum WebStreamState
{
    Idle,
    Waking,
    Connecting,
    Streaming,
}

public sealed record WebStreamQualityRequest(int Width, int Height, int Fps, int BitrateKbps, bool AdaptiveBitrate = false)
{
    public static readonly WebStreamQualityRequest Default = new(1920, 1080, 60, 12000, false);

    public WebStreamQualityRequest Normalize()
    {
        var (width, height) = Height switch
        {
            <= 360 => (640, 360),
            <= 540 => (960, 540),
            <= 720 => (1280, 720),
            _ => (1920, 1080),
        };
        var fps = Fps <= 30 ? 30 : 60;
        var bitrate = Math.Clamp(BitrateKbps, 2000, 100000);
        return new WebStreamQualityRequest(width, height, fps, bitrate, AdaptiveBitrate);
    }
}

public sealed record WebRoomMember(
    string Id,
    string Name,
    bool HasControl,
    string? Ip = null,
    string? Location = null,
    bool PsButtonUnlocked = false);

public sealed record WebPendingClient(
    string Id,
    string Name,
    string Ip,
    string Location,
    DateTimeOffset RequestedAtUtc);

public sealed record WebShareSecurityConfig(
    bool RequireApproval = false,
    bool RequireDailyPassword = false,
    bool LockPsButton = false,
    bool LockToCurrentGame = false,
    bool AllowRemoteRestMode = true,
    int MaxClients = 4,
    string? AdminKey = null,
    string? PsButtonUnlockPassword = null,
    int DailyPasswordEpoch = 0);

public sealed record WebShareStatus(
    string ConsoleName,
    string ConsoleState,
    WebStreamState StreamState,
    bool ControllerAllowed,
    int ClientCount,
    string? Codec,
    int Width,
    int Height,
    int AudioSampleRate,
    int AudioChannels,
    string? Error,
    string? StatusDetail = null,
    int CooldownSeconds = 0,
    IReadOnlyList<WebRoomMember>? Members = null,
    string? ControllerClientId = null,
    string? ControllerName = null,
    bool PsButtonLocked = false,
    bool RequireDailyPassword = false,
    bool RequireApproval = false,
    bool AllowRemoteRestMode = true,
    string? RunningApp = null,
    double EstimatedUploadMbps = 0,
    int MaxClients = 4,
    bool CanSaveClip = false,
    bool AdaptiveBitrate = false,
    int AdaptivePressurePercent = 0);

public sealed record WebAdminSnapshot(
    WebShareStatus Status,
    IReadOnlyList<WebPendingClient> Pending,
    IReadOnlyList<WebRoomMember> Members,
    string GuestToken,
    string? DailyPassword,
    bool RequireApproval,
    bool RequireDailyPassword,
    bool LockPsButton,
    bool LockToCurrentGame,
    bool AllowRemoteRestMode,
    string? LockedGameName,
    int MaxClients,
    string? LastMessage = null);

public sealed record WebAdminActionResult(bool Ok, string Message);

public sealed record WebClipSaveResult(bool Ok, string? FileName, string Message);

public interface IWebShareCallbacks
{
    WebShareStatus GetStatus();

    Task RequestWakeAndConnectAsync(WebStreamQualityRequest quality, CancellationToken cancellationToken);

    Task RequestDisconnectAsync(CancellationToken cancellationToken);

    void RequestKeyFrame();

    void OnRemoteControllerState(in ControllerState state);

    void OnRemoteControllerState(in ControllerState state, string clientId) => OnRemoteControllerState(state);

    Task RequestRestModeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task<string?> RequestSaveClipAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    async Task<WebClipSaveResult> RequestSaveClipDetailedAsync(CancellationToken cancellationToken)
    {
        var path = await RequestSaveClipAsync(cancellationToken);
        return !string.IsNullOrWhiteSpace(path)
            ? new WebClipSaveResult(true, Path.GetFileName(path), $"Clipe salvo no host: {Path.GetFileName(path)}")
            : new WebClipSaveResult(false, null, "Ainda não há frames suficientes para salvar o clipe.");
    }

    void OnClientPending(WebPendingClient pending)
    {
    }

    void OnClientJoined(WebRoomMember member)
    {
    }

    void OnClientLeft(string clientId, string name, string reason)
    {
    }

    void OnClientPsUnlocked(WebRoomMember member)
    {
    }

    Task<WebAdminActionResult> ExecuteAdminActionAsync(string action, string? targetId, string? value, CancellationToken cancellationToken) =>
        Task.FromResult(new WebAdminActionResult(false, "Ação não suportada neste modo."));

    string? GetDailyPassword() => null;

    string? GetLockedGameName() => null;

    void OnActiveControllerNetworkSignal(bool queueOverflow, int queueDepth, int rttMs, int decodeQueueDepth)
    {
    }

    void OnActiveControllerResumed()
    {
    }
}
