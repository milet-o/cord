using System.Diagnostics;
using System.Globalization;
using System.Net;
using Cord.Core.Settings;
using Cord.Core.Storage;
using Cord.Media;
using Cord.Protocol;
using Cord.Protocol.Av;
using Cord.Protocol.Crypto;
using Cord.Protocol.Discovery;
using Cord.Protocol.Input;
using Cord.Protocol.Registration;
using Cord.Protocol.Session;
using Cord.Protocol.Stream;
using Cord.WebShare;

namespace Cord.Core.Consoles;

/// <summary>
/// Exposes a paired console to friends in a web browser over Cloudflare Tunnel (Quick or Named),
/// enforcing waiting-room approval, daily passwords, PS-button / game-exit locking, Telegram /
/// mobile /admin control, rest mode, and 30s instant replay clips.
/// </summary>
public sealed class ConsoleWebShare : IWebShareCallbacks, ITelegramBotActions, IDisposable
{
    private static readonly TimeSpan WakeTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan WakeResendInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RemoteInputTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PostDisconnectCooldown = TimeSpan.FromSeconds(5);

    private readonly RpCryptTables _tables;
    private readonly ConsoleRegistry _registry;
    private readonly ConsoleBrowser _browser;
    private readonly SettingsStore? _settingsStore;
    private readonly Lock _sessionLock = new();
    private readonly ReplayBuffer _webReplayBuffer = new();
    private readonly Stopwatch _webReplayClock = Stopwatch.StartNew();

    private WebShareServer? _server;
    private CloudflareTunnel? _tunnel;
    private TelegramOwnerBot? _telegramBot;
    private ConsoleEntry? _entry;
    private bool _localStreamAttached;
    private Action? _localKeyFrameCallback;
    private Func<CancellationToken, Task>? _localRestCallback;
    private Func<string?>? _localClipCallback;
    private CancellationTokenSource? _webSessionCts;
    private Task? _webSessionTask;
    private RemotePlaySession? _webSession;
    private WebStreamState _webStreamState = WebStreamState.Idle;
    private string? _sessionError;
    private string? _statusDetail;
    private WebStreamQualityRequest _activeQuality = WebStreamQualityRequest.Default;
    private bool _controllerAllowed = true;
    private ControllerState _remoteInput = ControllerState.Idle;
    private long _lastRemoteInputTicks;
    private DateTimeOffset _cooldownUntilUtc = DateTimeOffset.MinValue;
    private string? _lockedGameName;
    private int _gameMismatchCount;
    private long _generation;
    private volatile bool _tunnelLaunching;
    private string? _cachedAdminKey;
    private readonly AdaptiveBitrateController _adaptiveController = new();
    private int _lastBroadcastAdaptivePressure;
    private long _lastAdaptiveStatusTicks;
    private long _lastActiveInputTicks;
    private long _lastIdleResumeKeyFrameTicks;

    public ConsoleWebShare(
        RpCryptTables tables,
        ConsoleRegistry registry,
        ConsoleBrowser browser,
        SettingsStore? settingsStore = null)
    {
        _tables = tables;
        _registry = registry;
        _browser = browser;
        _settingsStore = settingsStore;
    }

    public string? HostId { get; private set; }

    public string? ShareUrl
    {
        get
        {
            var server = _server;
            var baseUrl = _tunnel?.PublicBaseUrl;
            return server is null || baseUrl is null ? null : $"{baseUrl}/?t={server.Token}";
        }
    }

    public string? AdminUrl
    {
        get
        {
            var server = _server;
            var baseUrl = _tunnel?.PublicBaseUrl;
            return server is null || baseUrl is null ? null : $"{baseUrl}/admin?k={server.AdminKey}";
        }
    }

    public string? LocalUrl => _server?.LocalUrl;

    public string? LocalAdminUrl => _server?.LocalAdminUrl;

    public bool TunnelStarting => _tunnelLaunching || (_tunnel is not null && _tunnel.PublicBaseUrl is null && _tunnel.Error is null);

    public string? Error { get; private set; }

    public int ClientCount => _server?.ClientCount ?? 0;

    public int PendingCount => _server?.PendingCount ?? 0;

    public string? ActiveControllerName => _server?.ActiveControllerName;

    public IReadOnlyList<WebRoomMember> Members => _server?.GetRoomMembers() ?? [];

    public IReadOnlyList<WebPendingClient> PendingClients => _server?.GetPendingClients() ?? [];

    public string? LockedGameName => _lockedGameName;

    public string? RunningApp => _entry?.RunningApp;

    public bool TelegramActive => _telegramBot is not null;

    public string? DailyPassword
    {
        get
        {
            if (_server is null)
                return null;
            var epoch = CurrentWebShareSettings().DailyPasswordEpoch;
            return DailyPasswordGenerator.ComputeToday(_server.AdminKey, epoch);
        }
    }

    public double EstimatedUploadMbps =>
        StreamState == WebStreamState.Streaming
            ? (_activeQuality.BitrateKbps / 1000.0) * Math.Max(0, ClientCount)
            : 0;

    public bool ControllerAllowed
    {
        get => _controllerAllowed;
        set
        {
            if (_controllerAllowed == value)
                return;
            _controllerAllowed = value;
            if (!value)
            {
                _remoteInput = ControllerState.Idle;
                _webSession?.Stream.UpdateController(ControllerState.Idle);
            }
            else
            {
                _lockedGameName = null;
                _gameMismatchCount = 0;
                CaptureLockedGameIfNeeded(_entry?.RunningApp);
            }

            _server?.BroadcastStatus();
        }
    }

    public WebStreamState StreamState =>
        _localStreamAttached ? WebStreamState.Streaming : _webStreamState;

    public int CooldownRemainingSeconds
    {
        get
        {
            var remaining = _cooldownUntilUtc - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? (int)Math.Ceiling(remaining.TotalSeconds) : 0;
        }
    }

    public void Start(ConsoleEntry entry, bool startTunnel = true)
    {
        var previous = DetachRuntimeResources();
        if (previous.HasAny)
            _ = Task.Run(() => DisposeDetachedAsync(previous));

        Error = null;
        _sessionError = null;
        _lockedGameName = null;
        _gameMismatchCount = 0;
        _webReplayBuffer.Reset();

        if (!entry.Registered || entry.Address is null)
        {
            Error = $"{entry.Name} was not found on your network, so it cannot be shared.";
            return;
        }

        _entry = entry;
        HostId = entry.HostId;

        var secConfig = BuildSecurityConfig();
        var server = new WebShareServer(this, security: secConfig);
        server.ClientCountChanged += () => server.BroadcastStatus();
        _server = server;

        StartTelegramIfConfigured();

        if (!startTunnel)
            return;

        var cfg = CurrentWebShareSettings();
        var namedToken = SecretText.Unprotect(cfg.CloudflareTunnelTokenProtected);
        var namedUrl = cfg.CloudflarePublicUrl;
        var generation = Volatile.Read(ref _generation);
        var localPort = server.Port;

        _tunnelLaunching = true;
        _ = Task.Run(() => StartTunnelBackground(generation, localPort, namedToken, namedUrl));
    }

    public void RefreshSecurityFromSettings()
    {
        _server?.UpdateSecurityConfig(BuildSecurityConfig());
        var previousBot = Interlocked.Exchange(ref _telegramBot, null);
        if (previousBot is not null)
            _ = Task.Run(async () => await previousBot.DisposeAsync());
        StartTelegramIfConfigured();
    }

    /// <summary>
    /// Detaches Web Share state immediately without blocking the UI thread and disposes network/tunnel
    /// resources asynchronously in the background.
    /// </summary>
    public void RequestStop()
    {
        var detached = DetachRuntimeResources();
        if (detached.HasAny)
            _ = Task.Run(() => DisposeDetachedAsync(detached));
    }

    public void Stop()
    {
        var detached = DetachRuntimeResources();
        if (detached.HasAny)
            DisposeDetachedAsync(detached).GetAwaiter().GetResult();
    }

    private void StartTunnelBackground(long generation, int localPort, string? namedToken, string? namedUrl)
    {
        CloudflareTunnel? tunnel = null;
        try
        {
            var exe = CloudflareTunnel.FindExecutable();
            if (exe is null)
            {
                if (Volatile.Read(ref _generation) == generation)
                {
                    Error = "cloudflared não foi encontrado; apenas o link local está ativo.";
                    _tunnelLaunching = false;
                    _server?.BroadcastStatus();
                }

                return;
            }

            tunnel = !string.IsNullOrWhiteSpace(namedToken) && !string.IsNullOrWhiteSpace(namedUrl)
                ? CloudflareTunnel.StartNamed(namedToken, namedUrl, exe)
                : CloudflareTunnel.Start(localPort, exe);

            if (Volatile.Read(ref _generation) != generation)
            {
                tunnel.Dispose();
                return;
            }

            tunnel.PublicUrlReady += _ => _server?.BroadcastStatus();
            _tunnel = tunnel;
            _tunnelLaunching = false;
            _server?.BroadcastStatus();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            tunnel?.Dispose();
            if (Volatile.Read(ref _generation) == generation)
            {
                Error = $"Não foi possível iniciar o túnel Cloudflare: {e.Message}";
                _tunnelLaunching = false;
                _server?.BroadcastStatus();
            }
        }
    }

    private DetachedResources DetachRuntimeResources()
    {
        Interlocked.Increment(ref _generation);
        _tunnelLaunching = false;

        CancellationTokenSource? cts;
        Task? task;
        lock (_sessionLock)
        {
            cts = _webSessionCts;
            task = _webSessionTask;
            _webSessionCts = null;
            _webSessionTask = null;
        }

        var bot = Interlocked.Exchange(ref _telegramBot, null);
        var tunnel = Interlocked.Exchange(ref _tunnel, null);
        var server = Interlocked.Exchange(ref _server, null);

        HostId = null;
        _entry = null;
        _localStreamAttached = false;
        _localKeyFrameCallback = null;
        _localRestCallback = null;
        _localClipCallback = null;
        _webSession = null;
        _webStreamState = WebStreamState.Idle;
        _remoteInput = ControllerState.Idle;
        _sessionError = null;
        _statusDetail = null;
        _lockedGameName = null;
        _gameMismatchCount = 0;
        _webReplayBuffer.Reset();
        _adaptiveController.Reset();
        _lastBroadcastAdaptivePressure = 0;

        return new DetachedResources(cts, task, bot, tunnel, server);
    }

    private static async Task DisposeDetachedAsync(DetachedResources res)
    {
        if (res.SessionCts is not null)
        {
            await res.SessionCts.CancelAsync();
            if (res.SessionTask is not null)
            {
                try
                {
                    await res.SessionTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (Exception e) when (e is OperationCanceledException or TimeoutException)
                {
                }
            }

            res.SessionCts.Dispose();
        }

        if (res.TelegramBot is not null)
        {
            try
            {
                await res.TelegramBot.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
            }
        }

        res.Tunnel?.Dispose();

        if (res.Server is not null)
        {
            try
            {
                await res.Server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
            }
        }
    }

    private readonly record struct DetachedResources(
        CancellationTokenSource? SessionCts,
        Task? SessionTask,
        TelegramOwnerBot? TelegramBot,
        CloudflareTunnel? Tunnel,
        WebShareServer? Server)
    {
        public bool HasAny =>
            SessionCts is not null || TelegramBot is not null || Tunnel is not null || Server is not null;
    }

    public void ClearError()
    {
        Error = null;
        _sessionError = null;
    }

    public bool ApproveClient(string clientId, out string? error)
    {
        if (_server is null)
        {
            error = "Servidor web inativo.";
            return false;
        }

        return _server.ApproveClient(clientId, out error);
    }

    public bool DenyClient(string clientId) => _server?.DenyClient(clientId) ?? false;

    public bool KickClient(string clientId) => _server?.KickClient(clientId) ?? false;

    public bool AssignControl(string clientId) => _server?.AssignControl(clientId) ?? false;

    public string? RegenerateGuestToken() => _server?.RegenerateToken();

    public void RotateDailyPassword()
    {
        if (_settingsStore is null)
            return;
        var current = _settingsStore.Current;
        _settingsStore.Save(current with
        {
            WebShare = current.WebShare with { DailyPasswordEpoch = current.WebShare.DailyPasswordEpoch + 1 },
        });
        _server?.UpdateSecurityConfig(BuildSecurityConfig());
    }

    public bool PsButtonLocked
    {
        get => CurrentWebShareSettings().LockPsButton;
        set => UpdateWebShareSetting(w => w with { LockPsButton = value });
    }

    public bool LockToCurrentGame
    {
        get => CurrentWebShareSettings().LockToCurrentGame;
        set
        {
            UpdateWebShareSetting(w => w with { LockToCurrentGame = value });
            if (!value)
                _lockedGameName = null;
            else
                CaptureLockedGameIfNeeded(_entry?.RunningApp);
        }
    }

    public void Follow(IReadOnlyList<ConsoleEntry> entries)
    {
        if (HostId is null)
            return;

        var updated = entries.FirstOrDefault(e => e.Registered && string.Equals(e.HostId, HostId, StringComparison.OrdinalIgnoreCase));
        if (updated is null)
        {
            Stop();
            return;
        }

        var previousState = _entry?.State;
        var previousApp = _entry?.RunningApp;
        _entry = updated;
        EvaluateGameExitLock(updated.RunningApp);

        if (previousState != updated.State || !string.Equals(previousApp, updated.RunningApp, StringComparison.Ordinal))
            _server?.BroadcastStatus();
    }

    /// <summary>
    /// Disconnects any headless web-owned session before the host launches a local stream.
    /// Returns true if a web-owned session was active and had to be closed.
    /// </summary>
    public async Task<bool> DetachWebOwnedSessionForLocalPlayAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_sessionLock)
        {
            cts = _webSessionCts;
            task = _webSessionTask;
            _webSessionCts = null;
            _webSessionTask = null;
        }

        if (cts is null)
            return false;

        await cts.CancelAsync();
        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts.Dispose();
        _webStreamState = WebStreamState.Idle;
        return true;
    }

    public void AttachLocalStream(
        string hostId,
        Action requestKeyFrame,
        Func<CancellationToken, Task>? putToRest = null,
        Func<string?>? saveClip = null)
    {
        if (!string.Equals(HostId, hostId, StringComparison.OrdinalIgnoreCase))
            return;

        _localStreamAttached = true;
        _localKeyFrameCallback = requestKeyFrame;
        _localRestCallback = putToRest;
        _localClipCallback = saveClip;
        _sessionError = null;
        _statusDetail = null;
        CaptureLockedGameIfNeeded(_entry?.RunningApp);
        _server?.BroadcastStatus();
    }

    public void DetachLocalStream()
    {
        if (!_localStreamAttached)
            return;

        _localStreamAttached = false;
        _localKeyFrameCallback = null;
        _localRestCallback = null;
        _localClipCallback = null;
        _lockedGameName = null;
        _gameMismatchCount = 0;
        _remoteInput = ControllerState.Idle;
        _cooldownUntilUtc = DateTimeOffset.UtcNow + PostDisconnectCooldown;
        _server?.OnStreamEnded();
    }

    public void PublishStreamInfo(IReadOnlyList<VideoStreamProfile> profiles, AudioFormat audio, bool isHevc) =>
        _server?.OnStreamInfo(profiles, audio, isHevc);

    public void PublishVideoFrame(ReadOnlyMemory<byte> frame, ReadOnlyMemory<byte>? streamHeader) =>
        _server?.OnVideoFrame(frame, streamHeader);

    public void PublishAudioFrame(ReadOnlySpan<byte> opusFrame) =>
        _server?.OnAudioFrame(opusFrame);

    public void PublishRumble(byte left, byte right) =>
        _server?.OnRumble(left, right);

    public bool TryGetRemoteInput(out ControllerState state)
    {
        if (_controllerAllowed
            && ClientCount > 0
            && Environment.TickCount64 - Volatile.Read(ref _lastRemoteInputTicks) < (long)RemoteInputTimeout.TotalMilliseconds)
        {
            state = _remoteInput;
            return true;
        }

        state = ControllerState.Idle;
        return false;
    }

    public async Task PutConsoleToRestAsync(CancellationToken cancellationToken = default)
    {
        if (_localStreamAttached && _localRestCallback is not null)
        {
            await _localRestCallback(cancellationToken);
            return;
        }

        if (_webSession is { } activeWebSession)
        {
            await activeWebSession.Ctrl.GoToRestAsync(cancellationToken);
            await DetachWebOwnedSessionForLocalPlayAsync();
            return;
        }

        if (HostId is not { } hostId)
            throw new InvalidOperationException("Nenhum console selecionado.");

        RefreshEntry();
        var stored = _registry.Find(hostId);
        if (stored is null || _entry?.Address is not { } address)
            throw new InvalidOperationException("Console não encontrado na rede local.");

        if (_entry.State == ConsoleState.Standby)
            return;

        await PutConsoleToRestStandaloneAsync(_tables, address, stored.ToCredentials(), cancellationToken);
    }

    public static async Task PutConsoleToRestStandaloneAsync(
        RpCryptTables tables,
        IPAddress address,
        RegisteredConsole credentials,
        CancellationToken cancellationToken = default)
    {
        var nonce = await SessionRequest.RequestNonceAsync(new IPEndPoint(address, SessionRequest.DefaultPort), credentials.RegistKey, cancellationToken);
        using var cipher = RpCipher.ForSession(tables, nonce, credentials.RpKey);
        var secrets = SessionSecrets.Generate();
        await using var ctrl = await CtrlChannel.ConnectAsync(
            new IPEndPoint(address, CtrlChannel.DefaultPort),
            credentials.RegistKey,
            cipher,
            secrets,
            VideoCodec.H264,
            cancellationToken);
        _ = await ctrl.WaitForSessionIdAsync(null, TimeSpan.FromSeconds(5), cancellationToken);
        await ctrl.GoToRestAsync(cancellationToken);
    }

    public string? SaveClip()
    {
        if (_localStreamAttached && _localClipCallback is not null)
            return _localClipCallback();

        if (!_webReplayBuffer.HasClipData)
            return null;

        return _webReplayBuffer.SaveMp4();
    }

    public void Dispose() => Stop();

    WebShareStatus IWebShareCallbacks.GetStatus()
    {
        RefreshEntry();
        var entry = _entry;
        var cfg = CurrentWebShareSettings();
        var error = _sessionError ?? _tunnel?.Error ?? Error;
        return new WebShareStatus(
            entry?.Name ?? "PlayStation 5",
            entry?.State.ToString() ?? nameof(ConsoleState.Unknown),
            StreamState,
            _controllerAllowed,
            ClientCount,
            _server?.Codec,
            _server?.Width ?? 0,
            _server?.Height ?? 0,
            _server?.AudioSampleRate ?? 48000,
            _server?.AudioChannels ?? 2,
            error,
            _statusDetail,
            CooldownRemainingSeconds,
            _server?.GetRoomMembers(),
            _server?.ActiveControllerId,
            _server?.ActiveControllerName,
            cfg.LockPsButton,
            cfg.RequireDailyPassword,
            cfg.RequireApproval,
            cfg.AllowRemoteRestMode,
            entry?.RunningApp,
            EstimatedUploadMbps,
            Math.Clamp(cfg.MaxClients, 1, 8),
            _localStreamAttached ? _localClipCallback is not null : _webReplayBuffer.HasClipData,
            _activeQuality.AdaptiveBitrate,
            _activeQuality.AdaptiveBitrate ? _adaptiveController.PressurePercent : 0);
    }

    Task IWebShareCallbacks.RequestWakeAndConnectAsync(WebStreamQualityRequest quality, CancellationToken cancellationToken)
    {
        if (_localStreamAttached || HostId is null)
        {
            _localKeyFrameCallback?.Invoke();
            return Task.CompletedTask;
        }

        lock (_sessionLock)
        {
            if (_webSessionTask is { IsCompleted: false })
                return Task.CompletedTask;

            RefreshEntry();
            _sessionError = null;
            _activeQuality = quality.Normalize();
            _webStreamState = _entry?.State == ConsoleState.Ready ? WebStreamState.Connecting : WebStreamState.Waking;
            _statusDetail = _webStreamState == WebStreamState.Waking
                ? "Enviando sinal para ligar o PS5..."
                : "Conectando ao PS5...";
            _webSessionCts = new CancellationTokenSource();
            var ct = _webSessionCts.Token;
            var hostId = HostId;
            var targetQuality = _activeQuality;
            _webSessionTask = Task.Run(() => RunWebOwnedSessionAsync(hostId, targetQuality, ct), CancellationToken.None);
        }

        _server?.BroadcastStatus();
        return Task.CompletedTask;
    }

    Task IWebShareCallbacks.RequestDisconnectAsync(CancellationToken cancellationToken) =>
        _localStreamAttached ? Task.CompletedTask : DetachWebOwnedSessionForLocalPlayAsync();

    void IWebShareCallbacks.RequestKeyFrame()
    {
        if (_localStreamAttached)
            _localKeyFrameCallback?.Invoke();
        else if (_webSession is { } session)
            _ = session.Stream.RequestKeyFrameAsync();
    }

    void IWebShareCallbacks.OnRemoteControllerState(in ControllerState state)
    {
        if (!_controllerAllowed)
            return;

        var filtered = state;
        if (CurrentWebShareSettings().LockPsButton && filtered.Buttons.HasFlag(ControllerButtons.PS))
            filtered = filtered with { Buttons = filtered.Buttons & ~ControllerButtons.PS };

        _remoteInput = filtered;
        Volatile.Write(ref _lastRemoteInputTicks, Environment.TickCount64);
        if (filtered != ControllerState.Idle)
            NotifyActiveInputAndRequestResumeKeyframeIfIdle();
        if (!_localStreamAttached)
            _webSession?.Stream.UpdateController(filtered);
    }

    void IWebShareCallbacks.OnRemoteControllerState(in ControllerState state, string clientId)
    {
        if (!_controllerAllowed)
            return;

        var filtered = state;
        if (CurrentWebShareSettings().LockPsButton
            && !(_server?.IsClientPsButtonUnlocked(clientId) ?? false)
            && filtered.Buttons.HasFlag(ControllerButtons.PS))
        {
            filtered = filtered with { Buttons = filtered.Buttons & ~ControllerButtons.PS };
        }

        _remoteInput = filtered;
        Volatile.Write(ref _lastRemoteInputTicks, Environment.TickCount64);
        if (filtered != ControllerState.Idle)
            NotifyActiveInputAndRequestResumeKeyframeIfIdle();
        if (!_localStreamAttached)
            _webSession?.Stream.UpdateController(filtered);
    }

    private void NotifyActiveInputAndRequestResumeKeyframeIfIdle()
    {
        var now = Environment.TickCount64;
        var prevActive = Interlocked.Exchange(ref _lastActiveInputTicks, now);
        if (prevActive == 0 || now - prevActive < 4000)
            return;

        var lastKf = Volatile.Read(ref _lastIdleResumeKeyFrameTicks);
        if (now - lastKf < 2000 || Interlocked.CompareExchange(ref _lastIdleResumeKeyFrameTicks, now, lastKf) != lastKf)
            return;

        if (_localStreamAttached)
            _localKeyFrameCallback?.Invoke();
        else if (_webSession is { } session)
            _ = session.Stream.RequestKeyFrameAsync();
    }

    Task IWebShareCallbacks.RequestRestModeAsync(CancellationToken cancellationToken) =>
        PutConsoleToRestAsync(cancellationToken);

    Task<string?> IWebShareCallbacks.RequestSaveClipAsync(CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(SaveClip());
        }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or IOException)
        {
            return Task.FromResult<string?>(null);
        }
    }

    Task<WebClipSaveResult> IWebShareCallbacks.RequestSaveClipDetailedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var path = SaveClip();
            return Task.FromResult(path is not null
                ? new WebClipSaveResult(true, Path.GetFileName(path), $"Clipe salvo no host: {Path.GetFileName(path)}")
                : new WebClipSaveResult(false, null, "Ainda não há keyframe no buffer — aguarde 2 segundos e tente de novo."));
        }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or IOException)
        {
            return Task.FromResult(new WebClipSaveResult(false, null, $"Não foi possível salvar o clipe: {e.Message}"));
        }
    }

    void IWebShareCallbacks.OnClientPending(WebPendingClient pending) =>
        _ = _telegramBot?.NotifyPendingRequestAsync(pending);

    void IWebShareCallbacks.OnClientJoined(WebRoomMember member) =>
        _ = _telegramBot?.NotifyClientJoinedAsync(member);

    void IWebShareCallbacks.OnClientLeft(string clientId, string name, string reason) =>
        _ = _telegramBot?.NotifyClientLeftAsync(name, reason);

    void IWebShareCallbacks.OnClientPsUnlocked(WebRoomMember member) =>
        _ = _telegramBot?.NotifyPsUnlockedAsync(member);

    async Task<WebAdminActionResult> IWebShareCallbacks.ExecuteAdminActionAsync(
        string action,
        string? targetId,
        string? value,
        CancellationToken cancellationToken)
    {
        switch (action)
        {
            case "setControllerAllowed":
                ControllerAllowed = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                return new WebAdminActionResult(true, ControllerAllowed ? "Controle remoto liberado." : "Controle remoto travado.");
            case "setPsLock":
                PsButtonLocked = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                return new WebAdminActionResult(true, PsButtonLocked ? "Botão PS travado." : "Botão PS liberado.");
            case "setGameLock":
                LockToCurrentGame = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                return new WebAdminActionResult(true, LockToCurrentGame ? "Trava por jogo ativada." : "Trava por jogo desativada.");
            case "setRequireApproval":
                UpdateWebShareSetting(w => w with { RequireApproval = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) });
                return new WebAdminActionResult(true, "Configuração de aprovação atualizada.");
            case "setRequireDailyPassword":
                UpdateWebShareSetting(w => w with { RequireDailyPassword = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) });
                return new WebAdminActionResult(true, "Configuração de senha do dia atualizada.");
            case "rotateGuestToken":
                RegenerateGuestToken();
                return new WebAdminActionResult(true, "Novo link gerado! O link antigo foi invalidado.");
            case "rotateDailyPassword":
                RotateDailyPassword();
                return new WebAdminActionResult(true, $"Nova senha do dia: {DailyPassword}");
            case "disconnectStream":
                if (_localStreamAttached)
                    return new WebAdminActionResult(false, "Stream local em andamento no PC.");
                await DetachWebOwnedSessionForLocalPlayAsync();
                return new WebAdminActionResult(true, "Stream web encerrado.");
            case "saveClip":
                try
                {
                    var path = SaveClip();
                    return path is not null
                        ? new WebAdminActionResult(true, $"Clipe salvo: {Path.GetFileName(path)}")
                        : new WebAdminActionResult(false, "Ainda não há buffer suficiente para salvar o clipe.");
                }
                catch (InvalidOperationException ex)
                {
                    return new WebAdminActionResult(false, ex.Message);
                }
            case "restMode":
                try
                {
                    await PutConsoleToRestAsync(cancellationToken);
                    return new WebAdminActionResult(true, "PS5 colocado em Modo de Repouso.");
                }
                catch (Exception e) when (e is InvalidOperationException or RemotePlayException or IOException or System.Net.Sockets.SocketException)
                {
                    return new WebAdminActionResult(false, $"Falha ao colocar em repouso: {e.Message}");
                }
            default:
                return new WebAdminActionResult(false, "Ação desconhecida.");
        }
    }

    string? IWebShareCallbacks.GetDailyPassword() => DailyPassword;

    string? IWebShareCallbacks.GetLockedGameName() => _lockedGameName;

    void IWebShareCallbacks.OnActiveControllerNetworkSignal(bool queueOverflow, int queueDepth, int rttMs, int decodeQueueDepth)
    {
        if (!_activeQuality.AdaptiveBitrate || _localStreamAttached || _webSession is not { } session)
            return;

        var now = Environment.TickCount64;
        var loss = _adaptiveController.Observe(now, queueOverflow, queueDepth, rttMs, decodeQueueDepth);
        session.Stream.SetExternalCongestionLoss(loss);

        var pressure = _adaptiveController.PressurePercent;
        var lastPressure = _lastBroadcastAdaptivePressure;
        if (((pressure == 0) != (lastPressure == 0) || Math.Abs(pressure - lastPressure) >= 20)
            && now - Volatile.Read(ref _lastAdaptiveStatusTicks) >= 1000)
        {
            _lastBroadcastAdaptivePressure = pressure;
            Volatile.Write(ref _lastAdaptiveStatusTicks, now);
            _server?.BroadcastStatus();
        }
    }

    void IWebShareCallbacks.OnActiveControllerResumed()
    {
        _adaptiveController.Reset();
        if (!_localStreamAttached && _webSession is { } session)
        {
            session.Stream.SetExternalCongestionLoss(0);
            _ = session.Stream.RequestKeyFrameAsync();
        }

        if (_lastBroadcastAdaptivePressure != 0)
        {
            _lastBroadcastAdaptivePressure = 0;
            Volatile.Write(ref _lastAdaptiveStatusTicks, Environment.TickCount64);
            _server?.BroadcastStatus();
        }
    }

    WebAdminSnapshot ITelegramBotActions.GetSnapshot() =>
        _server?.BuildAdminSnapshot()
        ?? new WebAdminSnapshot(
            ((IWebShareCallbacks)this).GetStatus(),
            [],
            [],
            "",
            DailyPassword,
            CurrentWebShareSettings().RequireApproval,
            CurrentWebShareSettings().RequireDailyPassword,
            CurrentWebShareSettings().LockPsButton,
            CurrentWebShareSettings().LockToCurrentGame,
            CurrentWebShareSettings().AllowRemoteRestMode,
            _lockedGameName,
            Math.Clamp(CurrentWebShareSettings().MaxClients, 1, 8));

    Task<WebAdminActionResult> ITelegramBotActions.ExecuteActionAsync(
        string action,
        string? targetId,
        string? value,
        CancellationToken cancellationToken)
    {
        if (_server is null)
            return Task.FromResult(new WebAdminActionResult(false, "Web Share não está ativo."));

        switch (action)
        {
            case "approve" when !string.IsNullOrEmpty(targetId):
                return Task.FromResult(ApproveClient(targetId, out var err)
                    ? new WebAdminActionResult(true, "Jogador aceito na sala!")
                    : new WebAdminActionResult(false, err ?? "Não encontrado."));
            case "deny" when !string.IsNullOrEmpty(targetId):
                return Task.FromResult(DenyClient(targetId)
                    ? new WebAdminActionResult(true, "Solicitação recusada.")
                    : new WebAdminActionResult(false, "Solicitação não encontrada."));
            case "kick" when !string.IsNullOrEmpty(targetId):
                return Task.FromResult(KickClient(targetId)
                    ? new WebAdminActionResult(true, "Jogador expulso da sala.")
                    : new WebAdminActionResult(false, "Jogador não encontrado."));
            case "passControl" when !string.IsNullOrEmpty(targetId):
                return Task.FromResult(AssignControl(targetId)
                    ? new WebAdminActionResult(true, "Controle transferido.")
                    : new WebAdminActionResult(false, "Jogador não encontrado."));
            default:
                return ((IWebShareCallbacks)this).ExecuteAdminActionAsync(action, targetId, value, cancellationToken);
        }
    }

    private WebShareSettings CurrentWebShareSettings() =>
        _settingsStore?.Current.WebShare ?? new WebShareSettings { RequireApproval = false, LockPsButton = false, LockToCurrentGame = false };

    private void UpdateWebShareSetting(Func<WebShareSettings, WebShareSettings> updater)
    {
        if (_settingsStore is not null)
        {
            var current = _settingsStore.Current;
            _settingsStore.Save(current with { WebShare = updater(current.WebShare) });
        }

        _server?.UpdateSecurityConfig(BuildSecurityConfig());
    }

    private WebShareSecurityConfig BuildSecurityConfig()
    {
        var cfg = CurrentWebShareSettings();
        _cachedAdminKey ??= _settingsStore?.EnsureWebShareAdminKey();
        var psUnlockPass = SecretText.Unprotect(cfg.PsButtonUnlockPasswordProtected);
        return new WebShareSecurityConfig(
            cfg.RequireApproval,
            cfg.RequireDailyPassword,
            cfg.LockPsButton,
            cfg.LockToCurrentGame,
            cfg.AllowRemoteRestMode,
            Math.Clamp(cfg.MaxClients, 1, 8),
            _cachedAdminKey,
            psUnlockPass,
            cfg.DailyPasswordEpoch);
    }

    private void StartTelegramIfConfigured()
    {
        var cfg = CurrentWebShareSettings();
        var token = SecretText.Unprotect(cfg.TelegramBotTokenProtected);
        if (string.IsNullOrWhiteSpace(token)
            || string.IsNullOrWhiteSpace(cfg.TelegramChatId)
            || !long.TryParse(cfg.TelegramChatId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var chatId)
            || chatId == 0)
        {
            return;
        }

        _telegramBot = new TelegramOwnerBot(token, chatId, this);
    }

    private void CaptureLockedGameIfNeeded(string? runningApp)
    {
        if (!CurrentWebShareSettings().LockToCurrentGame || !_controllerAllowed)
            return;
        if (!string.IsNullOrWhiteSpace(runningApp) && string.IsNullOrWhiteSpace(_lockedGameName))
        {
            _lockedGameName = runningApp;
            _gameMismatchCount = 0;
        }
    }

    private void EvaluateGameExitLock(string? runningApp)
    {
        if (!CurrentWebShareSettings().LockToCurrentGame || StreamState != WebStreamState.Streaming)
        {
            _lockedGameName = null;
            _gameMismatchCount = 0;
            return;
        }

        if (string.IsNullOrWhiteSpace(_lockedGameName))
        {
            CaptureLockedGameIfNeeded(runningApp);
            return;
        }

        if (string.Equals(runningApp, _lockedGameName, StringComparison.OrdinalIgnoreCase))
        {
            _gameMismatchCount = 0;
            return;
        }

        _gameMismatchCount++;
        if (_gameMismatchCount < 2)
            return;

        var previous = _lockedGameName;
        _lockedGameName = null;
        _gameMismatchCount = 0;
        _controllerAllowed = false;
        _remoteInput = ControllerState.Idle;
        _webSession?.Stream.UpdateController(ControllerState.Idle);
        _statusDetail = $"Jogo '{previous}' foi fechado — controle travado por segurança.";
        _server?.BroadcastStatus();
        _ = _telegramBot?.NotifyGameExitAutoLockAsync(previous, runningApp);
    }

    private void RefreshEntry()
    {
        if (HostId is not { } hostId)
            return;

        var live = _browser.Snapshot().FirstOrDefault(e => e.Registered && string.Equals(e.HostId, hostId, StringComparison.OrdinalIgnoreCase));
        if (live is not null && (live.State != ConsoleState.Unknown || _entry is null))
        {
            _entry = live;
            EvaluateGameExitLock(live.RunningApp);
        }
    }

    private async Task RunWebOwnedSessionAsync(string hostId, WebStreamQualityRequest quality, CancellationToken cancellationToken)
    {
        var sessionEstablished = false;
        _adaptiveController.Reset();
        _lastBroadcastAdaptivePressure = 0;
        try
        {
            var stored = _registry.Find(hostId);
            RefreshEntry();
            var entry = _entry;
            if (stored is null || entry?.Address is null)
            {
                _sessionError = "Console não encontrado na rede do host.";
                return;
            }

            var credentials = stored.ToCredentials();
            var address = entry.Address;

            if (entry.State != ConsoleState.Ready)
            {
                _webStreamState = WebStreamState.Waking;
                _statusDetail = "Acordando o PS5 do modo de repouso...";
                _server?.BroadcastStatus();

                var credential = WakeupCredential.FromRegistKey(credentials.RegistKey);
                var startedAt = DateTime.UtcNow;
                var deadline = startedAt + WakeTimeout;
                var nextWake = DateTime.MinValue;

                while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
                {
                    RefreshEntry();
                    if (_entry?.State == ConsoleState.Ready)
                    {
                        address = _entry.Address ?? address;
                        break;
                    }

                    if (DateTime.UtcNow >= nextWake)
                    {
                        nextWake = DateTime.UtcNow + WakeResendInterval;
                        var elapsed = (int)(DateTime.UtcNow - startedAt).TotalSeconds;
                        _statusDetail = elapsed >= 2
                            ? $"Acordando o PS5... ({elapsed.ToString(CultureInfo.InvariantCulture)}s)"
                            : "Enviando sinal para ligar o PS5...";
                        _server?.BroadcastStatus();
                        try
                        {
                            await _browser.WakeAsync(address, credential, cancellationToken);
                        }
                        catch (Exception e) when (e is System.Net.Sockets.SocketException or InvalidOperationException)
                        {
                        }
                    }

                    await Task.Delay(250, cancellationToken);
                }

                RefreshEntry();
                if (_entry?.State != ConsoleState.Ready)
                {
                    _sessionError = "O PS5 não acordou a tempo. Verifique se o despertar pela rede está ativado no console.";
                    return;
                }

                _webStreamState = WebStreamState.Connecting;
                _statusDetail = "PS5 ligado! Aguardando serviço Remote Play iniciar...";
                _server?.BroadcastStatus();
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }

            while (CooldownRemainingSeconds is > 0 and var remaining && !cancellationToken.IsCancellationRequested)
            {
                _webStreamState = WebStreamState.Connecting;
                _statusDetail = $"Aguardando o PS5 liberar a sessão anterior ({remaining.ToString(CultureInfo.InvariantCulture)}s)...";
                _server?.BroadcastStatus();
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            _webStreamState = WebStreamState.Connecting;
            _statusDetail = "Conectando ao PS5...";
            _server?.BroadcastStatus();

            var options = new StreamOptions(quality.Width, quality.Height, quality.Fps, quality.BitrateKbps, VideoCodec.H264, DualSense: false);
            var sink = new WebSessionSink(this);
            await using var session = await ConnectWithRetriesAsync(address, credentials, options, sink, cancellationToken);
            sessionEstablished = true;

            _webSession = session;
            _statusDetail = null;
            _webStreamState = WebStreamState.Streaming;
            CaptureLockedGameIfNeeded(_entry?.RunningApp);
            _server?.BroadcastStatus();
            _server?.ResendCurrentConfigToApproved();
            _ = session.Stream.RequestKeyFrameAsync(cancellationToken);

            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancelReg = cancellationToken.Register(() => cancelled.TrySetResult());
            var endTask = session.WaitForEndAsync();
            if (await Task.WhenAny(endTask, cancelled.Task) == endTask)
            {
                var reason = await endTask;
                if (!cancellationToken.IsCancellationRequested && !string.IsNullOrWhiteSpace(reason))
                    _sessionError = reason;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is RemotePlayException or IOException or System.Net.Sockets.SocketException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            _sessionError = FormatStartupError(e);
        }
        finally
        {
            if (sessionEstablished)
                _cooldownUntilUtc = DateTimeOffset.UtcNow + PostDisconnectCooldown;
            _webSession = null;
            _webStreamState = WebStreamState.Idle;
            _statusDetail = null;
            _lockedGameName = null;
            _gameMismatchCount = 0;
            _remoteInput = ControllerState.Idle;
            _adaptiveController.Reset();
            _lastBroadcastAdaptivePressure = 0;
            _server?.OnStreamEnded();
        }
    }

    private async Task<RemotePlaySession> ConnectWithRetriesAsync(
        IPAddress address,
        RegisteredConsole credentials,
        StreamOptions options,
        WebSessionSink sink,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 15;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RemotePlaySession.ConnectAsync(
                    _tables,
                    address,
                    credentials,
                    options,
                    sink,
                    cancellationToken: cancellationToken);
            }
            catch (Exception e) when (attempt < maxAttempts
                && !cancellationToken.IsCancellationRequested
                && IsTransientStartupError(e))
            {
                var inUse = e is RemotePlayException { Error: RemotePlayError.InUse };
                var nextAttempt = (attempt + 1).ToString(CultureInfo.InvariantCulture);
                var total = maxAttempts.ToString(CultureInfo.InvariantCulture);
                _statusDetail = inUse
                    ? $"PS5 encerrando sessão anterior, tentando novamente ({nextAttempt}/{total})..."
                    : $"PS5 ainda inicializando o Remote Play, tentando novamente ({nextAttempt}/{total})...";
                _server?.BroadcastStatus();

                await Task.Delay(TimeSpan.FromMilliseconds(inUse ? 4000 : 2500), cancellationToken);
                RefreshEntry();
                address = _entry?.Address ?? address;
            }
        }
    }

    private static bool IsTransientStartupError(Exception e) =>
        e is RemotePlayException { Error: RemotePlayError.InUse or RemotePlayError.InvalidResponse }
            or IOException
            or System.Net.Sockets.SocketException;

    private static string FormatStartupError(Exception e) => e switch
    {
        RemotePlayException { Error: RemotePlayError.InUse } =>
            "O PS5 ainda está ocupado com outra sessão. Aguarde alguns segundos e tente novamente.",
        IOException or System.Net.Sockets.SocketException =>
            "O PS5 recusou ou encerrou a conexão de rede. Verifique se ele terminou de ligar e tente novamente.",
        _ => e.Message,
    };

    private sealed class WebSessionSink(ConsoleWebShare owner) : IStreamSink
    {
        public void OnStreamInfo(IReadOnlyList<VideoStreamProfile> profiles, AudioFormat audio)
        {
            owner._webReplayClock.Restart();
            if (profiles.Count > 0)
            {
                owner._webReplayBuffer.Configure(
                    VideoCodecKind.H264,
                    profiles[0].Width,
                    profiles[0].Height,
                    audio.SampleRate > 0 ? audio.SampleRate : 48000,
                    audio.Channels is >= 1 and <= 8 ? audio.Channels : 2,
                    profiles[0].Header);
            }

            owner._server?.OnStreamInfo(profiles, audio, isHevc: false);
        }

        public void OnVideoFrame(ReadOnlyMemory<byte> frame, ReadOnlyMemory<byte>? streamHeader, bool recoveredWithFec)
        {
            var hadClip = owner._webReplayBuffer.HasClipData;
            var isKey = AnnexBVideoInspector.IsKeyframe(frame.Span, isHevc: false);
            var tsUs = owner._webReplayClock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
            var hdr = streamHeader is { } sh ? sh.Span : ReadOnlySpan<byte>.Empty;
            owner._webReplayBuffer.AddVideoFrame(frame.Span, hdr, isKey, tsUs);
            if (!hadClip && owner._webReplayBuffer.HasClipData)
                owner._server?.BroadcastStatus();
            owner._server?.OnVideoFrame(frame, streamHeader);
        }

        public void OnAudioFrame(ReadOnlySpan<byte> frame, bool haptics)
        {
            if (haptics)
                return;
            var tsUs = owner._webReplayClock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
            owner._webReplayBuffer.AddAudioFrame(frame, tsUs);
            owner._server?.OnAudioFrame(frame);
        }

        public void OnRumble(byte left, byte right) => owner._server?.OnRumble(left, right);

        public void OnTriggerEffects(TriggerEffect left, TriggerEffect right)
        {
        }

        public void OnPadInfo(PadInfo info)
        {
        }

        public void OnDisconnected(string reason)
        {
            if (!string.IsNullOrWhiteSpace(reason))
                owner._sessionError = reason;
        }
    }
}
