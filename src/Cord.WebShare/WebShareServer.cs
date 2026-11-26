using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cord.Protocol.Av;
using Cord.Protocol.Input;

namespace Cord.WebShare;

/// <summary>
/// Pure-.NET HTTP/1.1 + RFC 6455 WebSocket server that serves the browser client and the owner's
/// mobile /admin PWA, enforces waiting-room / daily-password / token security, forwards raw
/// H.264/HEVC + Opus frames without transcoding, and receives browser gamepad/keyboard input.
/// </summary>
public sealed class WebShareServer : IAsyncDisposable
{
    private const int MaxHttpHeaderBytes = 16 * 1024;
    private const int MaxRequestBodyBytes = 16 * 1024;
    private const int KeyframeRequestCooldownMs = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Lazy<byte[]> IndexHtmlBytes = new(() => LoadHtmlTemplate("Cord.WebShare.Assets.index.html"));
    private static readonly Lazy<byte[]> AdminHtmlBytes = new(() => LoadHtmlTemplate("Cord.WebShare.Assets.admin.html"));
    private static readonly Lazy<byte[]> LogoPngBytes = new(() => LoadEmbeddedResource("Cord.WebShare.Assets.logo.png"));
    private static readonly byte[] SpeedTestChunk = CreateSpeedTestChunk();

    private readonly IWebShareCallbacks _callbacks;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, WebClient> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AdminSocket> _adminSockets = new(StringComparer.Ordinal);
    private readonly IpGeoResolver _geo = new();
    private readonly Lock _mediaLock = new();
    private readonly Lock _roomLock = new();
    private readonly Lock _securityLock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Task _acceptLoop;

    private string _token;
    private byte[] _tokenBytes;
    private WebShareSecurityConfig _security;
    private string _adminKey;
    private byte[] _adminKeyBytes;
    private byte[] _latestStreamHeader = [];
    private bool _isHevc;
    private string? _codec;
    private int _width;
    private int _height;
    private int _audioSampleRate = 48000;
    private int _audioChannels = 2;
    private long _lastKeyframeRequestTicks;
    private string? _activeControllerId;
    private long _joinCounter;
    private long _streamEpoch;
    private long _lastBackpressureSignalTicks;

    public WebShareServer(
        IWebShareCallbacks callbacks,
        string? token = null,
        IPAddress? bindAddress = null,
        int port = 0,
        WebShareSecurityConfig? security = null)
    {
        _callbacks = callbacks;
        _token = string.IsNullOrWhiteSpace(token) ? GenerateToken() : token;
        _tokenBytes = Encoding.UTF8.GetBytes(_token);
        _security = security ?? new WebShareSecurityConfig();
        _adminKey = string.IsNullOrWhiteSpace(_security.AdminKey) ? GenerateToken() : _security.AdminKey!;
        _adminKeyBytes = Encoding.UTF8.GetBytes(_adminKey);

        _listener = new TcpListener(bindAddress ?? IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stop.Token));
    }

    public static string AppVersion { get; } = ResolveAppVersion();

    public event Action? ClientCountChanged;

    public int Port { get; }

    public string Token
    {
        get
        {
            lock (_securityLock)
                return _token;
        }
    }

    public string AdminKey
    {
        get
        {
            lock (_securityLock)
                return _adminKey;
        }
    }

    public WebShareSecurityConfig Security
    {
        get
        {
            lock (_securityLock)
                return _security;
        }
    }

    public string LocalUrl => $"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}/?t={Token}";

    public string LocalAdminUrl => $"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}/admin?k={AdminKey}";

    public int ClientCount => _clients.Values.Count(c => c.ApprovalState == ClientApprovalState.Approved);

    public int PendingCount => _clients.Values.Count(c => c.ApprovalState == ClientApprovalState.Pending);

    public string? ActiveControllerId
    {
        get
        {
            lock (_roomLock)
                return _activeControllerId;
        }
    }

    public string? ActiveControllerName
    {
        get
        {
            var id = ActiveControllerId;
            return id is not null
                && _clients.TryGetValue(id, out var client)
                && client.ApprovalState == ClientApprovalState.Approved
                ? client.DisplayName
                : null;
        }
    }

    public void UpdateSecurityConfig(WebShareSecurityConfig config)
    {
        lock (_securityLock)
        {
            _security = config;
            if (!string.IsNullOrWhiteSpace(config.AdminKey))
            {
                _adminKey = config.AdminKey!;
                _adminKeyBytes = Encoding.UTF8.GetBytes(_adminKey);
            }
        }

        BroadcastStatus();
    }

    public string RegenerateToken()
    {
        string newToken;
        lock (_securityLock)
        {
            newToken = GenerateToken();
            _token = newToken;
            _tokenBytes = Encoding.UTF8.GetBytes(newToken);
        }

        BroadcastAdminState();
        return newToken;
    }

    public IReadOnlyList<WebRoomMember> GetRoomMembers()
    {
        var activeId = ActiveControllerId;
        return
        [
            .. _clients.Values
                .Where(c => c.ApprovalState == ClientApprovalState.Approved && c.DisplayName is not null)
                .OrderBy(c => c.JoinedOrder)
                .Select(c => new WebRoomMember(
                    c.ClientId,
                    c.DisplayName!,
                    string.Equals(c.ClientId, activeId, StringComparison.Ordinal),
                    c.Ip,
                    c.Location,
                    c.PsButtonUnlocked))
        ];
    }

    public IReadOnlyList<WebPendingClient> GetPendingClients() =>
        [
            .. _clients.Values
                .Where(c => c.ApprovalState == ClientApprovalState.Pending && c.DisplayName is not null)
                .OrderBy(c => c.RequestedAtUtc)
                .Select(c => new WebPendingClient(
                    c.ClientId,
                    c.DisplayName!,
                    c.Ip,
                    c.Location,
                    c.RequestedAtUtc))
        ];

    public bool IsClientPsButtonUnlocked(string? clientId) =>
        clientId is not null
        && _clients.TryGetValue(clientId, out var client)
        && client.PsButtonUnlocked;

    public bool ApproveClient(string clientId, out string? error)
    {
        error = null;
        if (!_clients.TryGetValue(clientId, out var client))
        {
            error = "Solicitação não encontrada ou o visitante saiu.";
            return false;
        }

        var maxClients = Math.Clamp(Security.MaxClients, 1, 8);
        WebRoomMember member;
        lock (_roomLock)
        {
            if (client.ApprovalState == ClientApprovalState.Approved)
            {
                error = "Visitante já está aprovado.";
                return true;
            }

            if (_clients.Values.Count(c => c.ApprovalState == ClientApprovalState.Approved) >= maxClients)
            {
                error = $"Limite da sala ({maxClients.ToString(CultureInfo.InvariantCulture)} pessoas) atingido.";
                return false;
            }

            PromoteToApprovedLocked(client);
            member = new WebRoomMember(
                client.ClientId,
                client.DisplayName ?? "Jogador",
                string.Equals(_activeControllerId, client.ClientId, StringComparison.Ordinal),
                client.Ip,
                client.Location,
                client.PsButtonUnlocked);
        }

        SendPostApprovalMessages(client);
        BroadcastStatus();
        ClientCountChanged?.Invoke();
        _callbacks.OnClientJoined(member);
        return true;
    }

    public bool DenyClient(string clientId, string reason = "Acesso recusado pelo dono do PS5.")
    {
        if (!_clients.TryRemove(clientId, out var client))
            return false;

        client.EnqueueText(JsonSerializer.Serialize(new { type = "kicked", reason }, JsonOptions));
        _ = DisposeClientLaterAsync(client);
        BroadcastStatus();
        ClientCountChanged?.Invoke();
        return true;
    }

    public bool KickClient(string clientId, string reason = "Você foi desconectado pelo dono do PS5.")
    {
        if (!_clients.TryRemove(clientId, out var client))
            return false;

        var name = client.DisplayName ?? "Jogador";
        var resetController = HandleClientRemovedFromRoom(clientId);
        client.EnqueueText(JsonSerializer.Serialize(new { type = "kicked", reason }, JsonOptions));
        _ = DisposeClientLaterAsync(client);

        if (resetController || ClientCount == 0)
            _callbacks.OnRemoteControllerState(ControllerState.Idle, clientId);
        BroadcastStatus();
        ClientCountChanged?.Invoke();
        _callbacks.OnClientLeft(clientId, name, "Expulso pelo dono");
        return true;
    }

    public bool AssignControl(string targetId)
    {
        var changed = false;
        lock (_roomLock)
        {
            if (_clients.TryGetValue(targetId, out var target)
                && target.ApprovalState == ClientApprovalState.Approved
                && target.DisplayName is not null)
            {
                _activeControllerId = targetId;
                changed = true;
            }
        }

        if (!changed)
            return false;

        _callbacks.OnRemoteControllerState(ControllerState.Idle, targetId);
        BroadcastStatus();
        return true;
    }

    public bool TryUnlockPsButton(string clientId, string? password)
    {
        var expected = Security.PsButtonUnlockPassword;
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(password))
            return false;

        var a = Encoding.UTF8.GetBytes(password.Trim());
        var b = Encoding.UTF8.GetBytes(expected.Trim());
        if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b))
            return false;

        if (!_clients.TryGetValue(clientId, out var client) || client.ApprovalState != ClientApprovalState.Approved)
            return false;

        client.PsButtonUnlocked = true;
        client.EnqueueText(JsonSerializer.Serialize(new { type = "psUnlocked", unlocked = true }, JsonOptions));
        var member = new WebRoomMember(
            client.ClientId,
            client.DisplayName ?? "Jogador",
            string.Equals(ActiveControllerId, client.ClientId, StringComparison.Ordinal),
            client.Ip,
            client.Location,
            true);
        _callbacks.OnClientPsUnlocked(member);
        BroadcastStatus();
        return true;
    }

    public string? Codec
    {
        get
        {
            lock (_mediaLock)
                return _codec;
        }
    }

    public int Width
    {
        get
        {
            lock (_mediaLock)
                return _width;
        }
    }

    public int Height
    {
        get
        {
            lock (_mediaLock)
                return _height;
        }
    }

    public int AudioSampleRate
    {
        get
        {
            lock (_mediaLock)
                return _audioSampleRate;
        }
    }

    public int AudioChannels
    {
        get
        {
            lock (_mediaLock)
                return _audioChannels;
        }
    }

    public void OnStreamInfo(IReadOnlyList<VideoStreamProfile> profiles, AudioFormat audio, bool isHevc)
    {
        string configJson;
        lock (_mediaLock)
        {
            _isHevc = isHevc;
            _audioSampleRate = audio.SampleRate > 0 ? audio.SampleRate : 48000;
            _audioChannels = audio.Channels is >= 1 and <= 8 ? audio.Channels : 2;
            if (profiles.Count > 0)
            {
                var first = profiles[0];
                _width = first.Width;
                _height = first.Height;
                _latestStreamHeader = [.. first.Header];
                _codec = AnnexBVideoInspector.ExtractCodecString(first.Header, isHevc);
            }
            else
            {
                _codec = isHevc ? "hev1.1.6.L120.90" : "avc1.4d002a";
            }

            _clock.Restart();
            _streamEpoch++;
            configJson = BuildConfigJsonLocked();
        }

        foreach (var client in _clients.Values)
            client.ResetKeyframeGate();

        BroadcastTextToApproved(configJson);
        BroadcastStatus();
    }

    public void OnVideoFrame(ReadOnlyMemory<byte> frame, ReadOnlyMemory<byte>? streamHeader)
    {
        byte[]? cachedHeader = null;
        bool isHevc;
        string? newConfigJson = null;

        lock (_mediaLock)
        {
            isHevc = _isHevc;
            if (streamHeader is { Length: > 0 } hdr)
            {
                _latestStreamHeader = hdr.ToArray();
                var extracted = AnnexBVideoInspector.ExtractCodecString(hdr.Span, isHevc);
                if (!string.Equals(extracted, _codec, StringComparison.Ordinal))
                {
                    _codec = extracted;
                    newConfigJson = BuildConfigJsonLocked();
                }
            }

            if (_latestStreamHeader.Length > 0)
                cachedHeader = _latestStreamHeader;
        }

        if (newConfigJson is not null)
            BroadcastTextToApproved(newConfigJson);

        if (_clients.IsEmpty)
            return;

        var isKeyframe = AnnexBVideoInspector.IsKeyframe(frame.Span, isHevc);
        var prepend = isKeyframe && cachedHeader is not null && !AnnexBVideoInspector.ContainsParameterSets(frame.Span, isHevc)
            ? cachedHeader
            : ReadOnlySpan<byte>.Empty;

        var timestampUs = _clock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
        var packet = WebMediaProtocol.BuildVideoPacket(frame.Span, prepend, isKeyframe, timestampUs);

        var needsKeyFrame = false;
        var activeControllerId = ActiveControllerId;
        foreach (var client in _clients.Values)
        {
            if (client.ApprovalState != ClientApprovalState.Approved)
                continue;
            var enqueued = client.TryEnqueueVideo(packet, isKeyframe);
            if (!enqueued)
                needsKeyFrame = true;

            if (activeControllerId is not null && string.Equals(client.ClientId, activeControllerId, StringComparison.Ordinal) && !client.IsTabHidden)
            {
                if (!enqueued)
                {
                    _callbacks.OnActiveControllerNetworkSignal(queueOverflow: true, queueDepth: 6, rttMs: -1, decodeQueueDepth: -1);
                }
                else
                {
                    var depth = client.VideoQueueDepth;
                    if (depth >= 4)
                    {
                        var now = Environment.TickCount64;
                        var prev = Volatile.Read(ref _lastBackpressureSignalTicks);
                        if (now - prev >= 250 && Interlocked.CompareExchange(ref _lastBackpressureSignalTicks, now, prev) == prev)
                            _callbacks.OnActiveControllerNetworkSignal(queueOverflow: false, queueDepth: depth, rttMs: -1, decodeQueueDepth: -1);
                    }
                }
            }
        }

        if (needsKeyFrame)
            TriggerKeyFrameRequest();
    }

    public void OnAudioFrame(ReadOnlySpan<byte> opusFrame)
    {
        if (_clients.IsEmpty || opusFrame.IsEmpty)
            return;

        var timestampUs = _clock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
        var packet = WebMediaProtocol.BuildAudioPacket(opusFrame, timestampUs);
        foreach (var client in _clients.Values)
        {
            if (client.ApprovalState == ClientApprovalState.Approved)
                client.EnqueueBinaryPriority(packet);
        }
    }

    public void OnRumble(byte left, byte right)
    {
        if (_clients.IsEmpty)
            return;

        var controllerId = ActiveControllerId;
        if (controllerId is null
            || !_clients.TryGetValue(controllerId, out var controllerClient)
            || controllerClient.ApprovalState != ClientApprovalState.Approved)
        {
            return;
        }

        controllerClient.EnqueueBinaryPriority(WebMediaProtocol.BuildRumblePacket(left, right));
    }

    public void OnStreamEnded()
    {
        lock (_mediaLock)
        {
            _latestStreamHeader = [];
            _codec = null;
            _width = 0;
            _height = 0;
        }

        foreach (var client in _clients.Values)
            client.ResetKeyframeGate();

        BroadcastStatus();
    }

    public void ResendCurrentConfigToApproved()
    {
        string? configJson = null;
        lock (_mediaLock)
        {
            if (_codec is not null)
                configJson = BuildConfigJsonLocked();
        }

        if (configJson is not null)
            BroadcastTextToApproved(configJson);
    }

    public void BroadcastStatus()
    {
        if (!_clients.IsEmpty)
        {
            var status = _callbacks.GetStatus();
            var approvedJson = JsonSerializer.Serialize(new { type = "status", status }, JsonOptions);
            var gateJson = JsonSerializer.Serialize(new
            {
                type = "status",
                status = status with { Error = null, Members = null, RunningApp = null },
            }, JsonOptions);

            foreach (var client in _clients.Values)
                client.EnqueueText(client.ApprovalState == ClientApprovalState.Approved ? approvedJson : gateJson);
        }

        BroadcastAdminState();
    }

    public void BroadcastAdminState(string? lastMessage = null)
    {
        if (_adminSockets.IsEmpty)
            return;

        var json = JsonSerializer.Serialize(new
        {
            type = "adminState",
            state = BuildAdminSnapshot(lastMessage),
        }, JsonOptions);

        foreach (var admin in _adminSockets.Values)
            admin.EnqueueText(json);
    }

    public WebAdminSnapshot BuildAdminSnapshot(string? lastMessage = null)
    {
        var security = Security;
        return new WebAdminSnapshot(
            _callbacks.GetStatus(),
            GetPendingClients(),
            GetRoomMembers(),
            Token,
            _callbacks.GetDailyPassword(),
            security.RequireApproval,
            security.RequireDailyPassword,
            security.LockPsButton,
            security.LockToCurrentGame,
            security.AllowRemoteRestMode,
            _callbacks.GetLockedGameName(),
            Math.Clamp(security.MaxClients, 1, 8),
            lastMessage);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        foreach (var client in _clients.Values)
            await client.DisposeAsync();
        _clients.Clear();

        foreach (var admin in _adminSockets.Values)
            await admin.DisposeAsync();
        _adminSockets.Clear();

        _geo.Dispose();
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                continue;
            }

            tcp.NoDelay = true;
            _ = Task.Run(() => HandleTcpClientAsync(tcp, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleTcpClientAsync(TcpClient tcp, CancellationToken cancellationToken)
    {
        var keepOpenForWebSocket = false;
        NetworkStream? stream = null;
        try
        {
            stream = tcp.GetStream();
            var request = await ReadHttpRequestAsync(stream, cancellationToken);
            if (request is null)
                return;

            if (IsAdminRoute(request.Path))
            {
                keepOpenForWebSocket = await HandleAdminHttpAsync(tcp, stream, request, cancellationToken);
                return;
            }

            if (!IsTokenValid(request))
            {
                await WriteHttpResponseAsync(stream, 403, "Forbidden", "text/plain; charset=utf-8", "Token invalido."u8.ToArray(), cancellationToken);
                return;
            }

            if (string.Equals(request.Path, "/", StringComparison.Ordinal) && string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteHttpResponseAsync(stream, 200, "OK", "text/html; charset=utf-8", IndexHtmlBytes.Value, cancellationToken);
                return;
            }

            if (string.Equals(request.Path, "/logo.png", StringComparison.Ordinal) && string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteHttpResponseAsync(stream, 200, "OK", "image/png", LogoPngBytes.Value, cancellationToken);
                return;
            }

            if (string.Equals(request.Path, "/api/status", StringComparison.Ordinal) && string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                var status = _callbacks.GetStatus();
                if (Security.RequireApproval && !HasApprovedRequester(request))
                    status = status with { Error = null, Members = null, RunningApp = null };
                var json = JsonSerializer.SerializeToUtf8Bytes(status, JsonOptions);
                await WriteHttpResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", json, cancellationToken);
                return;
            }

            if (string.Equals(request.Path, "/api/wake", StringComparison.Ordinal) && string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasControlPermission(request))
                {
                    await WriteHttpResponseAsync(stream, 403, "Forbidden", "text/plain; charset=utf-8", "Aguardando aprovacao."u8.ToArray(), cancellationToken);
                    return;
                }

                var quality = ParseQualityRequest(request.Body);
                await _callbacks.RequestWakeAndConnectAsync(quality, cancellationToken);
                var json = JsonSerializer.SerializeToUtf8Bytes(_callbacks.GetStatus(), JsonOptions);
                await WriteHttpResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", json, cancellationToken);
                return;
            }

            if (string.Equals(request.Path, "/api/disconnect", StringComparison.Ordinal) && string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasControlPermission(request))
                {
                    await WriteHttpResponseAsync(stream, 403, "Forbidden", "text/plain; charset=utf-8", "Sem permissao."u8.ToArray(), cancellationToken);
                    return;
                }

                await _callbacks.RequestDisconnectAsync(cancellationToken);
                var json = JsonSerializer.SerializeToUtf8Bytes(_callbacks.GetStatus(), JsonOptions);
                await WriteHttpResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", json, cancellationToken);
                return;
            }

            if (string.Equals(request.Path, "/api/rest", StringComparison.Ordinal) && string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                if (!Security.AllowRemoteRestMode || !HasControlPermission(request))
                {
                    await WriteHttpResponseAsync(stream, 403, "Forbidden", "text/plain; charset=utf-8", "Modo repouso remoto bloqueado pelo dono."u8.ToArray(), cancellationToken);
                    return;
                }

                await _callbacks.RequestRestModeAsync(cancellationToken);
                var json = JsonSerializer.SerializeToUtf8Bytes(new { ok = true, message = "Comando de repouso enviado ao PS5." }, JsonOptions);
                await WriteHttpResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", json, cancellationToken);
                return;
            }

            if (string.Equals(request.Path, "/api/clip", StringComparison.Ordinal) && string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                if (Security.RequireApproval && !HasApprovedRequester(request))
                {
                    await WriteHttpResponseAsync(stream, 403, "Forbidden", "text/plain; charset=utf-8", "Sem permissao."u8.ToArray(), cancellationToken);
                    return;
                }

                var result = await _callbacks.RequestSaveClipDetailedAsync(cancellationToken);
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    ok = result.Ok,
                    file = result.FileName,
                    message = result.Message,
                }, JsonOptions);
                await WriteHttpResponseAsync(stream, result.Ok ? 200 : 400, result.Ok ? "OK" : "Bad Request", "application/json; charset=utf-8", payload, cancellationToken);
                return;
            }

            if (string.Equals(request.Path, "/api/speedtest", StringComparison.Ordinal) && string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                var kb = 2048;
                if (request.QueryParams.TryGetValue("kb", out var kbText)
                    && int.TryParse(kbText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedKb))
                {
                    kb = Math.Clamp(parsedKb, 256, 8192);
                }

                await WriteSpeedTestResponseAsync(stream, kb * 1024, cancellationToken);
                return;
            }

            if (string.Equals(request.Path, "/ws", StringComparison.Ordinal)
                && string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)
                && request.Headers.TryGetValue("Upgrade", out var upgrade)
                && upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase)
                && request.Headers.TryGetValue("Sec-WebSocket-Key", out var wsKey)
                && !string.IsNullOrWhiteSpace(wsKey))
            {
                var remoteIp = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address;
                var clientIp = IpGeoResolver.ExtractClientIp(request.Headers, remoteIp);
                request.Headers.TryGetValue("CF-IPCountry", out var countryHint);

                await AcceptWebSocketHandshakeAsync(stream, wsKey, cancellationToken);
                keepOpenForWebSocket = true;
                var ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(15));
                await RunWebSocketClientAsync(tcp, ws, clientIp, countryHint, cancellationToken);
                return;
            }

            await WriteHttpResponseAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", "Not found"u8.ToArray(), cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (InvalidDataException)
        {
        }
        finally
        {
            if (!keepOpenForWebSocket)
            {
                stream?.Dispose();
                tcp.Dispose();
            }
        }
    }

    private async Task<bool> HandleAdminHttpAsync(TcpClient tcp, NetworkStream stream, HttpRequestData request, CancellationToken cancellationToken)
    {
        if (!IsAdminKeyValid(request))
        {
            await WriteHttpResponseAsync(stream, 403, "Forbidden", "text/plain; charset=utf-8", "Chave de admin invalida."u8.ToArray(), cancellationToken);
            return false;
        }

        if (string.Equals(request.Path, "/admin", StringComparison.Ordinal) && string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            await WriteHttpResponseAsync(stream, 200, "OK", "text/html; charset=utf-8", AdminHtmlBytes.Value, cancellationToken);
            return false;
        }

        if (string.Equals(request.Path, "/admin/logo.png", StringComparison.Ordinal) && string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            await WriteHttpResponseAsync(stream, 200, "OK", "image/png", LogoPngBytes.Value, cancellationToken);
            return false;
        }

        if (string.Equals(request.Path, "/admin/manifest.json", StringComparison.Ordinal) && string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            var key = Uri.EscapeDataString(AdminKey);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                name = "Cord Admin PS5",
                short_name = "Cord Admin",
                start_url = $"/admin?k={key}",
                display = "standalone",
                background_color = "#0F1114",
                theme_color = "#0F1114",
                icons = new[]
                {
                    new { src = $"/admin/logo.png?k={key}", sizes = "128x128", type = "image/png" },
                },
            }, JsonOptions);
            await WriteHttpResponseAsync(stream, 200, "OK", "application/manifest+json; charset=utf-8", manifest, cancellationToken);
            return false;
        }

        if (string.Equals(request.Path, "/admin/api/state", StringComparison.Ordinal) && string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(BuildAdminSnapshot(), JsonOptions);
            await WriteHttpResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", json, cancellationToken);
            return false;
        }

        if (string.Equals(request.Path, "/admin/api/action", StringComparison.Ordinal) && string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            var (action, targetId, value) = ParseAdminAction(request.Body);
            var result = await ExecuteAdminActionCoreAsync(action, targetId, value, cancellationToken);
            var json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                result.Ok,
                result.Message,
                state = BuildAdminSnapshot(result.Message),
            }, JsonOptions);
            await WriteHttpResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", json, cancellationToken);
            return false;
        }

        if (string.Equals(request.Path, "/admin/ws", StringComparison.Ordinal)
            && string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)
            && request.Headers.TryGetValue("Upgrade", out var upgrade)
            && upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase)
            && request.Headers.TryGetValue("Sec-WebSocket-Key", out var wsKey)
            && !string.IsNullOrWhiteSpace(wsKey))
        {
            await AcceptWebSocketHandshakeAsync(stream, wsKey, cancellationToken);
            var ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(15));
            await RunAdminWebSocketAsync(tcp, ws, cancellationToken);
            return true;
        }

        await WriteHttpResponseAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", "Not found"u8.ToArray(), cancellationToken);
        return false;
    }

    private async Task<WebAdminActionResult> ExecuteAdminActionCoreAsync(string action, string? targetId, string? value, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case "approve":
                if (string.IsNullOrWhiteSpace(targetId))
                    return new WebAdminActionResult(false, "ID inválido.");
                return ApproveClient(targetId, out var err)
                    ? new WebAdminActionResult(true, "Jogador aprovado e conectado na sala.")
                    : new WebAdminActionResult(false, err ?? "Não foi possível aprovar.");
            case "deny":
                if (string.IsNullOrWhiteSpace(targetId))
                    return new WebAdminActionResult(false, "ID inválido.");
                return DenyClient(targetId)
                    ? new WebAdminActionResult(true, "Solicitação recusada.")
                    : new WebAdminActionResult(false, "Solicitação não encontrada.");
            case "kick":
                if (string.IsNullOrWhiteSpace(targetId))
                    return new WebAdminActionResult(false, "ID inválido.");
                return KickClient(targetId)
                    ? new WebAdminActionResult(true, "Visitante desconectado da sala.")
                    : new WebAdminActionResult(false, "Visitante não encontrado.");
            case "passControl":
                if (string.IsNullOrWhiteSpace(targetId))
                    return new WebAdminActionResult(false, "ID inválido.");
                return AssignControl(targetId)
                    ? new WebAdminActionResult(true, "Controle transferido.")
                    : new WebAdminActionResult(false, "Visitante não encontrado.");
            default:
                var res = await _callbacks.ExecuteAdminActionAsync(action, targetId, value, cancellationToken);
                BroadcastAdminState(res.Message);
                return res;
        }
    }

    private async Task RunAdminWebSocketAsync(TcpClient tcp, WebSocket ws, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var admin = new AdminSocket(id, tcp, ws);
        _adminSockets[id] = admin;
        try
        {
            admin.EnqueueText(JsonSerializer.Serialize(new
            {
                type = "adminState",
                state = BuildAdminSnapshot(),
            }, JsonOptions));
            await admin.RunAsync(cancellationToken);
        }
        finally
        {
            _adminSockets.TryRemove(id, out _);
            await admin.DisposeAsync();
        }
    }

    private async Task RunWebSocketClientAsync(TcpClient tcp, WebSocket ws, string ip, string? countryHint, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var initialLocation = IpGeoResolver.IsPrivateOrLoopback(ip) ? "Rede local" : "Consultando localizacao...";
        var client = new WebClient(id, tcp, ws, this, ip, initialLocation);
        client.GeoTask = ResolveClientLocationAsync(client, ip, countryHint, cancellationToken);
        _clients[id] = client;
        ClientCountChanged?.Invoke();

        try
        {
            BroadcastStatus();
            await client.RunAsync(cancellationToken);
        }
        finally
        {
            var wasApproved = client.ApprovalState == ClientApprovalState.Approved;
            var name = client.DisplayName;
            _clients.TryRemove(id, out _);
            await client.DisposeAsync();

            var resetController = HandleClientRemovedFromRoom(id);
            if (resetController || ClientCount == 0)
                _callbacks.OnRemoteControllerState(ControllerState.Idle, id);
            BroadcastStatus();
            ClientCountChanged?.Invoke();
            if (wasApproved && name is not null)
                _callbacks.OnClientLeft(id, name, "Desconectou");
        }
    }

    private async Task ResolveClientLocationAsync(WebClient client, string ip, string? countryHint, CancellationToken cancellationToken)
    {
        var location = await _geo.ResolveLocationAsync(ip, countryHint, cancellationToken);
        client.Location = location;
        if (client.ApprovalState is ClientApprovalState.Pending or ClientApprovalState.Approved)
            BroadcastAdminState();
    }

    private bool HandleClientRemovedFromRoom(string clientId)
    {
        lock (_roomLock)
        {
            if (!string.Equals(_activeControllerId, clientId, StringComparison.Ordinal))
                return false;

            var next = _clients.Values
                .Where(c => c.ApprovalState == ClientApprovalState.Approved && c.DisplayName is not null)
                .OrderBy(c => c.JoinedOrder)
                .FirstOrDefault();
            _activeControllerId = next?.ClientId;
            return true;
        }
    }

    private async Task HandleJoinAsync(WebClient client, string? rawName, string? password)
    {
        var security = Security;
        var name = SanitizePlayerName(rawName);

        if (security.RequireDailyPassword)
        {
            var date = DateOnly.FromDateTime(DateTimeOffset.Now.DateTime);
            var valid = DailyPasswordGenerator.Verify(password, _adminKey, date, security.DailyPasswordEpoch);
            if (!valid)
            {
                client.EnqueueText(JsonSerializer.Serialize(new
                {
                    type = "joinError",
                    message = "Senha do dia incorreta. Peça o código atualizado ao dono do PS5.",
                }, JsonOptions));
                return;
            }
        }

        if (client.GeoTask is { IsCompleted: false } geoTask)
        {
            try
            {
                await geoTask.WaitAsync(TimeSpan.FromMilliseconds(900), _stop.Token);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        var maxClients = Math.Clamp(security.MaxClients, 1, 8);
        if (client.ApprovalState != ClientApprovalState.Approved && ClientCount >= maxClients)
        {
            client.EnqueueText(JsonSerializer.Serialize(new
            {
                type = "joinError",
                message = $"A sala já está cheia ({maxClients.ToString(CultureInfo.InvariantCulture)} pessoas).",
            }, JsonOptions));
            return;
        }

        if (security.RequireApproval && client.ApprovalState != ClientApprovalState.Approved)
        {
            client.DisplayName = name;
            client.ApprovalState = ClientApprovalState.Pending;
            client.RequestedAtUtc = DateTimeOffset.UtcNow;
            client.EnqueueText(JsonSerializer.Serialize(new
            {
                type = "pending",
                clientId = client.ClientId,
                name,
            }, JsonOptions));
            BroadcastAdminState();
            _callbacks.OnClientPending(new WebPendingClient(
                client.ClientId,
                name,
                client.Ip,
                client.Location,
                client.RequestedAtUtc));
            return;
        }

        WebRoomMember member;
        lock (_roomLock)
        {
            client.DisplayName = name;
            PromoteToApprovedLocked(client);
            member = new WebRoomMember(
                client.ClientId,
                name,
                string.Equals(_activeControllerId, client.ClientId, StringComparison.Ordinal),
                client.Ip,
                client.Location,
                client.PsButtonUnlocked);
        }

        SendPostApprovalMessages(client);
        BroadcastStatus();
        ClientCountChanged?.Invoke();
        _callbacks.OnClientJoined(member);
    }

    private void PromoteToApprovedLocked(WebClient client)
    {
        if (client.ApprovalState != ClientApprovalState.Approved)
        {
            client.JoinedOrder = Interlocked.Increment(ref _joinCounter);
            client.ApprovalState = ClientApprovalState.Approved;
        }

        if (_activeControllerId is null
            || !_clients.TryGetValue(_activeControllerId, out var current)
            || current.ApprovalState != ClientApprovalState.Approved)
        {
            _activeControllerId = client.ClientId;
        }
    }

    private void SendPostApprovalMessages(WebClient client)
    {
        client.EnqueueText(JsonSerializer.Serialize(new
        {
            type = "welcome",
            clientId = client.ClientId,
            name = client.DisplayName ?? "Jogador",
        }, JsonOptions));

        string? configJson = null;
        lock (_mediaLock)
        {
            if (_codec is not null)
                configJson = BuildConfigJsonLocked();
        }

        if (configJson is not null)
        {
            client.EnqueueText(configJson);
            TriggerKeyFrameRequest();
        }
    }

    private void PassControl(string requesterId, string targetId)
    {
        var changed = false;
        lock (_roomLock)
        {
            if (string.Equals(_activeControllerId, requesterId, StringComparison.Ordinal)
                && _clients.TryGetValue(targetId, out var target)
                && target.ApprovalState == ClientApprovalState.Approved
                && target.DisplayName is not null)
            {
                _activeControllerId = targetId;
                changed = true;
            }
        }

        if (!changed)
            return;

        _callbacks.OnRemoteControllerState(ControllerState.Idle, requesterId);
        BroadcastStatus();
    }

    private bool HasApprovedRequester(HttpRequestData request)
    {
        if (request.Headers.TryGetValue("X-Cord-Client-Id", out var clientId))
        {
            var trimmed = clientId.Trim();
            if (_clients.TryGetValue(trimmed, out var client) && client.ApprovalState == ClientApprovalState.Approved)
                return true;
        }

        return ClientCount > 0;
    }

    private bool HasControlPermission(HttpRequestData request)
    {
        if (!Security.RequireApproval)
            return true;

        if (!_callbacks.GetStatus().ControllerAllowed)
            return false;

        if (request.Headers.TryGetValue("X-Cord-Client-Id", out var clientId))
        {
            var trimmed = clientId.Trim();
            if (_clients.TryGetValue(trimmed, out var client) && client.ApprovalState == ClientApprovalState.Approved)
            {
                var activeId = ActiveControllerId;
                return activeId is null || string.Equals(activeId, trimmed, StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static string SanitizePlayerName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return "Jogador";

        var trimmed = rawName.Trim();
        var filtered = new string([.. trimmed.Where(c => !char.IsControl(c))]);
        if (filtered.Length == 0)
            return "Jogador";
        return filtered.Length > 24 ? filtered[..24] : filtered;
    }

    private void TriggerKeyFrameRequest()
    {
        var now = Environment.TickCount64;
        var prev = Volatile.Read(ref _lastKeyframeRequestTicks);
        if (now - prev < KeyframeRequestCooldownMs)
            return;
        if (Interlocked.CompareExchange(ref _lastKeyframeRequestTicks, now, prev) == prev)
            _callbacks.RequestKeyFrame();
    }

    private void BroadcastTextToApproved(string json)
    {
        foreach (var client in _clients.Values)
        {
            if (client.ApprovalState == ClientApprovalState.Approved)
                client.EnqueueText(json);
        }
    }

    private string BuildConfigJsonLocked() => JsonSerializer.Serialize(new
    {
        type = "config",
        codec = _codec,
        width = _width,
        height = _height,
        sampleRate = _audioSampleRate,
        channels = _audioChannels,
        epoch = _streamEpoch,
    }, JsonOptions);

    private void SendCurrentConfigTo(WebClient client)
    {
        string? configJson = null;
        lock (_mediaLock)
        {
            if (_codec is not null)
                configJson = BuildConfigJsonLocked();
        }

        if (configJson is not null)
            client.EnqueueText(configJson);
    }

    private bool IsTokenValid(HttpRequestData request)
    {
        request.QueryParams.TryGetValue("t", out var candidate);
        if (string.IsNullOrEmpty(candidate) && request.Headers.TryGetValue("X-Cord-Token", out var headerValue))
            candidate = headerValue.Trim();

        if (string.IsNullOrEmpty(candidate))
            return false;

        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        lock (_securityLock)
        {
            return candidateBytes.Length == _tokenBytes.Length
                && CryptographicOperations.FixedTimeEquals(candidateBytes, _tokenBytes);
        }
    }

    private bool IsAdminKeyValid(HttpRequestData request)
    {
        request.QueryParams.TryGetValue("k", out var candidate);
        if (string.IsNullOrEmpty(candidate) && request.Headers.TryGetValue("X-Cord-Admin-Key", out var headerValue))
            candidate = headerValue.Trim();

        if (string.IsNullOrEmpty(candidate))
            return false;

        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        lock (_securityLock)
        {
            return candidateBytes.Length == _adminKeyBytes.Length
                && CryptographicOperations.FixedTimeEquals(candidateBytes, _adminKeyBytes);
        }
    }

    private static bool IsAdminRoute(string path) =>
        string.Equals(path, "/admin", StringComparison.Ordinal)
        || path.StartsWith("/admin/", StringComparison.Ordinal);

    private static async Task AcceptWebSocketHandshakeAsync(NetworkStream stream, string wsKey, CancellationToken cancellationToken)
    {
        var accept = ComputeWebSocketAccept(wsKey.Trim());
        var handshake =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static WebStreamQualityRequest ParseQualityRequest(byte[]? body)
    {
        if (body is null || body.Length == 0)
            return WebStreamQualityRequest.Default;

        try
        {
            using var doc = JsonDocument.Parse(body);
            return ParseQualityFromElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return WebStreamQualityRequest.Default;
        }
    }

    private static WebStreamQualityRequest ParseQualityFromElement(JsonElement root)
    {
        var width = root.TryGetProperty("width", out var w) && w.TryGetInt32(out var wi) ? wi : WebStreamQualityRequest.Default.Width;
        var height = root.TryGetProperty("height", out var h) && h.TryGetInt32(out var hi) ? hi : WebStreamQualityRequest.Default.Height;
        var fps = root.TryGetProperty("fps", out var f) && f.TryGetInt32(out var fi) ? fi : WebStreamQualityRequest.Default.Fps;
        var bitrate = root.TryGetProperty("bitrateKbps", out var b) && b.TryGetInt32(out var bi) ? bi : WebStreamQualityRequest.Default.BitrateKbps;
        var adaptive = root.TryGetProperty("adaptiveBitrate", out var ab) && ab.ValueKind == JsonValueKind.True;
        return new WebStreamQualityRequest(width, height, fps, bitrate, adaptive).Normalize();
    }

    private static (string Action, string? TargetId, string? Value) ParseAdminAction(byte[]? body)
    {
        if (body is null || body.Length == 0)
            return ("", null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var action = root.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
            var targetId = root.TryGetProperty("targetId", out var t) ? t.GetString() : null;
            var value = root.TryGetProperty("value", out var v) ? v.GetString() : null;
            return (action, targetId, value);
        }
        catch (JsonException)
        {
            return ("", null, null);
        }
    }

    private static async Task DisposeClientLaterAsync(WebClient client)
    {
        await Task.Delay(250);
        await client.DisposeAsync();
    }

    private static async Task<HttpRequestData?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            if (buffer.Length > MaxHttpHeaderBytes)
                throw new InvalidDataException("HTTP header too large.");

            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                return null;

            buffer.Write(chunk, 0, read);
            headerEnd = FindHeaderEnd(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }

        var raw = buffer.GetBuffer();
        var headerText = Encoding.ASCII.GetString(raw, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return null;

        var requestLine = lines[0].Split(' ', 3);
        if (requestLine.Length < 2)
            return null;

        var method = requestLine[0];
        var target = requestLine[1];
        var (path, queryParams) = ParseTarget(target);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
                continue;
            var name = lines[i][..colon].Trim();
            var value = lines[i][(colon + 1)..].Trim();
            headers[name] = value;
        }

        byte[]? body = null;
        if (headers.TryGetValue("Transfer-Encoding", out var transferEncoding)
            && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            var initial = raw.AsMemory(headerEnd, (int)buffer.Length - headerEnd);
            body = await ReadChunkedBodyAsync(stream, initial, cancellationToken);
        }
        else if (headers.TryGetValue("Content-Length", out var lengthText)
            && int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var contentLength)
            && contentLength > 0)
        {
            if (contentLength > MaxRequestBodyBytes)
                throw new InvalidDataException("Request body too large.");

            body = new byte[contentLength];
            var alreadyRead = Math.Min((int)buffer.Length - headerEnd, contentLength);
            if (alreadyRead > 0)
                Buffer.BlockCopy(raw, headerEnd, body, 0, alreadyRead);
            if (alreadyRead < contentLength)
                await stream.ReadExactlyAsync(body.AsMemory(alreadyRead), cancellationToken);
        }

        return new HttpRequestData(method, path, queryParams, headers, body);
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(
        NetworkStream stream,
        ReadOnlyMemory<byte> initial,
        CancellationToken cancellationToken)
    {
        var pending = new List<byte>(initial.Length + 256);
        pending.AddRange(initial.Span);
        using var decoded = new MemoryStream();
        var buf = new byte[1024];

        while (true)
        {
            var lineEnd = -1;
            while ((lineEnd = FindCrLf(pending)) < 0)
            {
                var read = await stream.ReadAsync(buf, cancellationToken);
                if (read == 0)
                    return decoded.ToArray();
                pending.AddRange(buf.AsSpan(0, read));
                if (pending.Count > MaxRequestBodyBytes)
                    throw new InvalidDataException("Chunk header too large.");
            }

            var sizeLine = Encoding.ASCII.GetString(pending.ToArray(), 0, lineEnd).Split(';', 2)[0].Trim();
            pending.RemoveRange(0, lineEnd + 2);
            if (!int.TryParse(sizeLine, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var chunkSize) || chunkSize < 0)
                throw new InvalidDataException("Invalid chunk size.");

            if (chunkSize == 0)
                return decoded.ToArray();

            if (decoded.Length + chunkSize > MaxRequestBodyBytes)
                throw new InvalidDataException("Chunked request body too large.");

            while (pending.Count < chunkSize + 2)
            {
                var read = await stream.ReadAsync(buf, cancellationToken);
                if (read == 0)
                    throw new InvalidDataException("Unexpected end of chunked stream.");
                pending.AddRange(buf.AsSpan(0, read));
            }

            decoded.Write(pending.ToArray(), 0, chunkSize);
            pending.RemoveRange(0, chunkSize + 2);
        }
    }

    private static int FindCrLf(List<byte> data)
    {
        for (var i = 0; i <= data.Count - 2; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n')
                return i;
        }

        return -1;
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i <= data.Length - 4; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i + 4;
        }

        return -1;
    }

    private static (string Path, Dictionary<string, string> QueryParams) ParseTarget(string target)
    {
        var queryParams = new Dictionary<string, string>(StringComparer.Ordinal);
        var q = target.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
            return (target, queryParams);

        var path = target[..q];
        var query = target[(q + 1)..];
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
                continue;
            var key = Uri.UnescapeDataString(pair[..eq]);
            queryParams[key] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }

        return (path, queryParams);
    }

    private static async Task WriteSpeedTestResponseAsync(NetworkStream stream, int totalBytes, CancellationToken cancellationToken)
    {
        var head =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            "Content-Encoding: identity\r\n" +
            $"Content-Length: {totalBytes.ToString(CultureInfo.InvariantCulture)}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken);
        var remaining = totalBytes;
        while (remaining > 0)
        {
            var chunk = Math.Min(remaining, SpeedTestChunk.Length);
            await stream.WriteAsync(SpeedTestChunk.AsMemory(0, chunk), cancellationToken);
            remaining -= chunk;
        }

        await stream.FlushAsync(cancellationToken);
    }

    private static byte[] CreateSpeedTestChunk()
    {
        var bytes = new byte[64 * 1024];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static async Task WriteHttpResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        string contentType,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var head =
            $"HTTP/1.1 {statusCode.ToString(CultureInfo.InvariantCulture)} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken);
        if (body.Length > 0)
            await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string ComputeWebSocketAccept(string key)
    {
        var combined = Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11");
#pragma warning disable CA5350 // RFC 6455 mandates SHA-1 for Sec-WebSocket-Accept
        var hash = SHA1.HashData(combined);
#pragma warning restore CA5350
        return Convert.ToBase64String(hash);
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(18);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string ResolveAppVersion()
    {
        var assembly = typeof(WebShareServer).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return $"v{(plus >= 0 ? informational[..plus] : informational)}";
        }

        var version = assembly.GetName().Version;
        return version is null ? "v0.0.0" : $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    private static byte[] LoadHtmlTemplate(string resourceName)
    {
        var html = Encoding.UTF8.GetString(LoadEmbeddedResource(resourceName));
        return Encoding.UTF8.GetBytes(html.Replace("{{CORD_VERSION}}", AppVersion, StringComparison.Ordinal));
    }

    private static byte[] LoadEmbeddedResource(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' was not found.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private enum ClientApprovalState
    {
        Unjoined,
        Pending,
        Approved,
    }

    private sealed record HttpRequestData(
        string Method,
        string Path,
        Dictionary<string, string> QueryParams,
        Dictionary<string, string> Headers,
        byte[]? Body);

    private readonly record struct OutgoingMessage(WebSocketMessageType Type, byte[] Data);

    private sealed class AdminSocket : IAsyncDisposable
    {
        private readonly string _id;
        private readonly TcpClient _tcp;
        private readonly WebSocket _ws;
        private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        private readonly CancellationTokenSource _stop = new();
        private int _disposed;

        public AdminSocket(string id, TcpClient tcp, WebSocket ws)
        {
            _id = id;
            _tcp = tcp;
            _ws = ws;
        }

        public string Id => _id;

        public void EnqueueText(string json)
        {
            if (!_stop.IsCancellationRequested)
                _outgoing.Writer.TryWrite(Encoding.UTF8.GetBytes(json));
        }

        public async Task RunAsync(CancellationToken serverCancellation)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation, _stop.Token);
            var sendTask = SendLoopAsync(linked.Token);
            var receiveTask = ReceiveLoopAsync(linked.Token);
            await Task.WhenAny(sendTask, receiveTask);
            await linked.CancelAsync();
            try
            {
                await Task.WhenAll(sendTask, receiveTask);
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await _stop.CancelAsync();
            _outgoing.Writer.TryComplete();
            try
            {
                if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", timeout.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
            }

            _ws.Dispose();
            _tcp.Dispose();
            _stop.Dispose();
        }

        private async Task SendLoopAsync(CancellationToken ct)
        {
            await foreach (var data in _outgoing.Reader.ReadAllAsync(ct))
            {
                if (_ws.State != WebSocketState.Open)
                    return;
                await _ws.SendAsync(data, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buf = new byte[1024];
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var res = await _ws.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close)
                    return;
            }
        }
    }

    private sealed class WebClient : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly WebSocket _ws;
        private readonly WebShareServer _server;
        private readonly Channel<OutgoingMessage> _priority = Channel.CreateBounded<OutgoingMessage>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        private readonly Channel<byte[]> _video = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(6) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _stop = new();
        private volatile bool _awaitingKeyframe = true;
        private int _disposed;

        public WebClient(string clientId, TcpClient tcp, WebSocket ws, WebShareServer server, string ip, string location)
        {
            ClientId = clientId;
            _tcp = tcp;
            _ws = ws;
            _server = server;
            Ip = ip;
            Location = location;
        }

        public string ClientId { get; }

        public string Ip { get; }

        public string Location { get; set; }

        public Task? GeoTask { get; set; }

        public string? DisplayName { get; set; }

        public long JoinedOrder { get; set; }

        public DateTimeOffset RequestedAtUtc { get; set; }

        public ClientApprovalState ApprovalState { get; set; } = ClientApprovalState.Unjoined;

        public bool PsButtonUnlocked { get; set; }

        public volatile bool IsTabHidden;

        public int VideoQueueDepth => _video.Reader.Count;

        public void ResetKeyframeGate() => _awaitingKeyframe = true;

        public void EnqueueText(string json)
        {
            if (_stop.IsCancellationRequested)
                return;
            if (_priority.Writer.TryWrite(new OutgoingMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json))))
                ReleaseSignal();
        }

        public void EnqueueBinaryPriority(byte[] data)
        {
            if (_stop.IsCancellationRequested)
                return;
            if (_priority.Writer.TryWrite(new OutgoingMessage(WebSocketMessageType.Binary, data)))
                ReleaseSignal();
        }

        public bool TryEnqueueVideo(byte[] packet, bool isKeyframe)
        {
            if (_stop.IsCancellationRequested)
                return true;

            if (_awaitingKeyframe)
            {
                if (!isKeyframe)
                    return true;
                _awaitingKeyframe = false;
            }

            if (_video.Writer.TryWrite(packet))
            {
                ReleaseSignal();
                return true;
            }

            _awaitingKeyframe = true;
            return false;
        }

        public async Task RunAsync(CancellationToken serverCancellation)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation, _stop.Token);
            var sendTask = SendLoopAsync(linked.Token);
            var receiveTask = ReceiveLoopAsync(linked.Token);
            await Task.WhenAny(sendTask, receiveTask);
            await linked.CancelAsync();
            try
            {
                await Task.WhenAll(sendTask, receiveTask);
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await _stop.CancelAsync();
            _priority.Writer.TryComplete();
            _video.Writer.TryComplete();
            try
            {
                if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", timeout.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
            }

            _ws.Dispose();
            _tcp.Dispose();
            _signal.Dispose();
            _stop.Dispose();
        }

        private void ReleaseSignal()
        {
            try
            {
                _signal.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task SendLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(cancellationToken);

                while (true)
                {
                    while (_priority.Reader.TryRead(out var msg))
                    {
                        if (_ws.State != WebSocketState.Open)
                            return;
                        await _ws.SendAsync(msg.Data, msg.Type, endOfMessage: true, cancellationToken);
                    }

                    if (_video.Reader.TryRead(out var videoPacket))
                    {
                        if (_ws.State != WebSocketState.Open)
                            return;
                        await _ws.SendAsync(videoPacket, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
                        continue;
                    }

                    if (_priority.Reader.Count == 0 && _video.Reader.Count == 0)
                        break;
                }
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            using var messageStream = new MemoryStream();

            while (!cancellationToken.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                messageStream.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    if (messageStream.Length > MaxRequestBodyBytes)
                        return;
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    var payload = messageStream.GetBuffer().AsSpan(0, (int)messageStream.Length);
                    if (ApprovalState == ClientApprovalState.Approved
                        && string.Equals(_server.ActiveControllerId, ClientId, StringComparison.Ordinal)
                        && WebMediaProtocol.TryParseControllerInput(payload, out var controllerState))
                    {
                        _server._callbacks.OnRemoteControllerState(controllerState, ClientId);
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    await HandleTextMessageAsync(messageStream.ToArray());
                }

                messageStream.SetLength(0);
            }
        }

        private async Task HandleTextMessageAsync(byte[] payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp))
                    return;

                var type = typeProp.GetString();
                if (type == "join")
                {
                    var rawName = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    var password = root.TryGetProperty("password", out var passProp) ? passProp.GetString() : null;
                    await _server.HandleJoinAsync(this, rawName, password);
                    return;
                }

                if (type == "ping")
                {
                    var ts = root.TryGetProperty("ts", out var tsProp) && tsProp.TryGetInt64(out var val) ? val : 0;
                    EnqueueText(JsonSerializer.Serialize(new { type = "pong", ts }, JsonOptions));
                    return;
                }

                if (ApprovalState != ClientApprovalState.Approved)
                    return;

                switch (type)
                {
                    case "passControl":
                        var targetId = root.TryGetProperty("targetId", out var targetProp) ? targetProp.GetString() : null;
                        if (!string.IsNullOrEmpty(targetId))
                            _server.PassControl(ClientId, targetId);
                        break;
                    case "unlockPs":
                        var psPass = root.TryGetProperty("password", out var psProp) ? psProp.GetString() : null;
                        if (!_server.TryUnlockPsButton(ClientId, psPass))
                        {
                            EnqueueText(JsonSerializer.Serialize(new
                            {
                                type = "psUnlockResult",
                                ok = false,
                                message = "Senha do botão PS incorreta ou não configurada.",
                            }, JsonOptions));
                        }
                        break;
                    case "requestKeyframe":
                        _awaitingKeyframe = true;
                        _server.SendCurrentConfigTo(this);
                        _server.TriggerKeyFrameRequest();
                        break;
                    case "netStats":
                        if (!IsTabHidden && string.Equals(_server.ActiveControllerId, ClientId, StringComparison.Ordinal))
                        {
                            var rttMs = root.TryGetProperty("rttMs", out var rttProp) && rttProp.TryGetInt32(out var r)
                                ? Math.Clamp(r, -1, 10000)
                                : -1;
                            var decodeQueue = root.TryGetProperty("decodeQueue", out var dqProp) && dqProp.TryGetInt32(out var dq)
                                ? Math.Clamp(dq, 0, 32)
                                : -1;
                            _server._callbacks.OnActiveControllerNetworkSignal(false, VideoQueueDepth, rttMs, decodeQueue);
                        }

                        break;
                    case "visibility":
                        var hidden = root.TryGetProperty("hidden", out var hiddenProp) && hiddenProp.ValueKind == JsonValueKind.True;
                        IsTabHidden = hidden;
                        if (!hidden && string.Equals(_server.ActiveControllerId, ClientId, StringComparison.Ordinal))
                        {
                            _awaitingKeyframe = true;
                            _server.SendCurrentConfigTo(this);
                            _server.TriggerKeyFrameRequest();
                            _server._callbacks.OnActiveControllerResumed();
                        }

                        break;
                    case "wake":
                        if (string.Equals(_server.ActiveControllerId, ClientId, StringComparison.Ordinal))
                        {
                            var quality = ParseQualityFromElement(root);
                            _ = _server._callbacks.RequestWakeAndConnectAsync(quality, CancellationToken.None);
                        }
                        break;
                    case "disconnect":
                        if (string.Equals(_server.ActiveControllerId, ClientId, StringComparison.Ordinal))
                            _ = _server._callbacks.RequestDisconnectAsync(CancellationToken.None);
                        break;
                }
            }
            catch (JsonException)
            {
            }
        }
    }
}
