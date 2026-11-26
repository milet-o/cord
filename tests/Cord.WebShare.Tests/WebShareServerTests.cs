using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Cord.Protocol.Av;
using Cord.Protocol.Input;

namespace Cord.WebShare.Tests;

public sealed class WebShareServerTests
{
    [Fact]
    public async Task HttpRoutes_EnforceTokenAndServeHtmlLogoStatusAndSpeedtest()
    {
        var callbacks = new FakeCallbacks();
        await using var server = new WebShareServer(callbacks, token: "test-token-123");
        using var http = new HttpClient();

        var forbidden = await http.GetAsync($"http://127.0.0.1:{server.Port}/");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var wrongToken = await http.GetAsync($"http://127.0.0.1:{server.Port}/api/status?t=wrong");
        Assert.Equal(HttpStatusCode.Forbidden, wrongToken.StatusCode);

        var htmlResp = await http.GetAsync(server.LocalUrl);
        Assert.Equal(HttpStatusCode.OK, htmlResp.StatusCode);
        var html = await htmlResp.Content.ReadAsStringAsync();
        Assert.Contains("VideoDecoder", html, StringComparison.Ordinal);
        Assert.Contains("webgl2", html, StringComparison.Ordinal);
        Assert.Contains("gpuBoost", html, StringComparison.Ordinal);
        Assert.Contains("frameGenBtn", html, StringComparison.Ordinal);
        Assert.Contains("data-fg-mode=\"extrapolation\"", html, StringComparison.Ordinal);
        Assert.Contains("data-fg-mode=\"interpolation\"", html, StringComparison.Ordinal);
        Assert.Contains("cord_frame_gen", html, StringComparison.Ordinal);
        Assert.Contains("cord_vsync", html, StringComparison.Ordinal);
        Assert.Contains("lobbyVsyncToggle", html, StringComparison.Ordinal);
        Assert.Contains("hudVsyncToggle", html, StringComparison.Ordinal);
        Assert.Contains("lobbyVsyncWarn", html, StringComparison.Ordinal);
        Assert.Contains("uFgMode", html, StringComparison.Ordinal);
        Assert.Contains("250 Hz", html, StringComparison.Ordinal);
        Assert.Contains("adaptiveBitrate", html, StringComparison.Ordinal);
        Assert.Contains("leaveBtn", html, StringComparison.Ordinal);
        Assert.Contains("endSessionLobbyBtn", html, StringComparison.Ordinal);
        Assert.Equal("v0.1.9", WebShareServer.AppVersion);
        Assert.Contains(WebShareServer.AppVersion, html, StringComparison.Ordinal);
        Assert.DoesNotContain("{{CORD_VERSION}}", html, StringComparison.Ordinal);
        Assert.Contains("preserveDrawingBuffer: true", html, StringComparison.Ordinal);
        Assert.DoesNotContain("desynchronized: true", html, StringComparison.Ordinal);
        Assert.DoesNotContain("getImageData", html, StringComparison.Ordinal);
        Assert.Contains("PIXEL_PACK_BUFFER", html, StringComparison.Ordinal);
        Assert.Contains("uSplit30", html, StringComparison.Ordinal);
        Assert.Contains("visibility", html, StringComparison.Ordinal);

        var logoBytes = await http.GetByteArrayAsync($"http://127.0.0.1:{server.Port}/logo.png?t={server.Token}");
        Assert.True(logoBytes.Length > 8);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], logoBytes[..4]);

        var statusJson = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/api/status?t={server.Token}");
        Assert.Contains("PS5 Sala", statusJson, StringComparison.Ordinal);

        var speedForbidden = await http.GetAsync($"http://127.0.0.1:{server.Port}/api/speedtest?kb=256");
        Assert.Equal(HttpStatusCode.Forbidden, speedForbidden.StatusCode);

        var speedBytes = await http.GetByteArrayAsync($"http://127.0.0.1:{server.Port}/api/speedtest?kb=256&t={server.Token}");
        Assert.Equal(256 * 1024, speedBytes.Length);
    }

    [Fact]
    public void QualityNormalize_AllowsUpTo100MbpsAndClampsAbove()
    {
        Assert.Equal(50000, new WebStreamQualityRequest(1280, 720, 60, 50000).Normalize().BitrateKbps);
        Assert.Equal(100000, new WebStreamQualityRequest(1920, 1080, 60, 150000).Normalize().BitrateKbps);
        Assert.Equal(2000, new WebStreamQualityRequest(1280, 720, 30, 500).Normalize().BitrateKbps);
        Assert.True(new WebStreamQualityRequest(1920, 1080, 60, 50000, AdaptiveBitrate: true).Normalize().AdaptiveBitrate);
    }

    [Fact]
    public async Task ReconnectAndRequestKeyframe_ResendConfigWithIncrementedEpochAndResetGate()
    {
        var callbacks = new FakeCallbacks();
        await using var server = new WebShareServer(callbacks, token: "reconn-tok");
        callbacks.Server = server;

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws?t={server.Token}"), CancellationToken.None);
        await JoinAsync(ws, "Player1");

        byte[] sps1080 = [0x00, 0x00, 0x00, 0x01, 0x67, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x01, 0x68, 0xEE];
        server.OnStreamInfo([new VideoStreamProfile(1920, 1080, sps1080)], new AudioFormat(2, 16, 48000, 480), isHevc: false);

        var cfg1Json = await ReceiveTextMatchingAsync(ws, "\"config\"");
        using var cfg1Doc = JsonDocument.Parse(cfg1Json);
        var epoch1 = cfg1Doc.RootElement.GetProperty("epoch").GetInt64();
        Assert.Equal(1080, cfg1Doc.RootElement.GetProperty("height").GetInt32());

        server.OnStreamEnded();
        await ReceiveTextMatchingAsync(ws, "\"status\"");

        byte[] sps720 = [0x00, 0x00, 0x00, 0x01, 0x67, 0x4D, 0x00, 0x20, 0x00, 0x00, 0x00, 0x01, 0x68, 0xEE];
        server.OnStreamInfo([new VideoStreamProfile(1280, 720, sps720)], new AudioFormat(2, 16, 48000, 480), isHevc: false);

        var cfg2Json = await ReceiveTextMatchingAsync(ws, "\"config\"");
        using var cfg2Doc = JsonDocument.Parse(cfg2Json);
        Assert.True(cfg2Doc.RootElement.GetProperty("epoch").GetInt64() > epoch1);
        Assert.Equal(720, cfg2Doc.RootElement.GetProperty("height").GetInt32());

        // Delta frame after reconnect must be dropped until IDR arrives
        server.OnVideoFrame(new byte[] { 0x00, 0x00, 0x00, 0x01, 0x41, 0x9A }, null);
        server.OnVideoFrame(new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84 }, null);
        var firstVideo = await ReceiveBinaryAsync(ws, WebMediaProtocol.VideoFrame);
        Assert.Equal(WebMediaProtocol.VideoFlagKeyframe, firstVideo[1]);

        await ws.SendAsync("""{"type":"requestKeyframe"}"""u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
        var resentCfg = await ReceiveTextMatchingAsync(ws, "\"config\"");
        Assert.Contains("\"height\":720", resentCfg, StringComparison.Ordinal);
        var deadline = Environment.TickCount64 + 1000;
        while (callbacks.KeyFrameRequests < 1 && Environment.TickCount64 < deadline)
            await Task.Delay(10);
        Assert.True(callbacks.KeyFrameRequests >= 1);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task AdaptiveWakeAndNetStats_ForwardActiveControllerOnly()
    {
        var callbacks = new FakeCallbacks();
        await using var server = new WebShareServer(callbacks, token: "adapt-tok");
        callbacks.Server = server;

        using var http = new HttpClient();
        var wakeResp = await http.PostAsJsonAsync(
            $"http://127.0.0.1:{server.Port}/api/wake?t={server.Token}",
            new { width = 1920, height = 1080, fps = 60, bitrateKbps = 50000, adaptiveBitrate = true });
        Assert.Equal(HttpStatusCode.OK, wakeResp.StatusCode);
        Assert.NotNull(callbacks.LastWakeQuality);
        Assert.True(callbacks.LastWakeQuality!.AdaptiveBitrate);
        Assert.Equal(50000, callbacks.LastWakeQuality.BitrateKbps);

        using var wsController = new ClientWebSocket();
        await wsController.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws?t={server.Token}"), CancellationToken.None);
        await JoinAsync(wsController, "Player1");

        using var wsSpectator = new ClientWebSocket();
        await wsSpectator.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws?t={server.Token}"), CancellationToken.None);
        await JoinAsync(wsSpectator, "Player2");

        // Spectator telemetry is ignored
        await wsSpectator.SendAsync("""{"type":"netStats","rttMs":999,"decodeQueue":5}"""u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
        await Task.Delay(80);
        Assert.Empty(callbacks.NetworkSignals);

        // Active controller telemetry is forwarded
        await wsController.SendAsync("""{"type":"netStats","rttMs":120,"decodeQueue":3}"""u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
        var signal = await callbacks.WaitForNetworkSignalAsync();
        Assert.Equal(120, signal.RttMs);
        Assert.Equal(3, signal.DecodeQueueDepth);

        await wsSpectator.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await wsController.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task ClipRoute_SurfacesDetailedErrorAndSuccessMessages()
    {
        var callbacks = new FakeCallbacks();
        await using var server = new WebShareServer(callbacks, token: "clip-tok");
        using var http = new HttpClient();

        callbacks.NextClipResult = new WebClipSaveResult(false, null, "Não foi possível salvar o clipe: FFmpeg não encontrado.");
        var failResp = await http.PostAsync($"http://127.0.0.1:{server.Port}/api/clip?t={server.Token}", null);
        Assert.Equal(HttpStatusCode.BadRequest, failResp.StatusCode);
        using (var failDoc = JsonDocument.Parse(await failResp.Content.ReadAsStringAsync()))
        {
            Assert.False(failDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("FFmpeg não encontrado", failDoc.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        callbacks.NextClipResult = new WebClipSaveResult(true, "Cord-20261125-220000.mp4", "Clipe salvo no host: Cord-20261125-220000.mp4");
        var okResp = await http.PostAsync($"http://127.0.0.1:{server.Port}/api/clip?t={server.Token}", null);
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        using (var okDoc = JsonDocument.Parse(await okResp.Content.ReadAsStringAsync()))
        {
            Assert.True(okDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("Cord-20261125-220000.mp4", okDoc.RootElement.GetProperty("file").GetString());
        }
    }

    [Fact]
    public async Task WebSocket_StreamsMediaWithKeyframeGatingAndReceivesInput()
    {
        var callbacks = new FakeCallbacks();
        await using var server = new WebShareServer(callbacks, token: "secret-tok");

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws?t={server.Token}"), CancellationToken.None);

        var statusMsg = await ReceiveTextAsync(ws);
        Assert.Contains("\"type\":\"status\"", statusMsg, StringComparison.Ordinal);

        await JoinAsync(ws, "Player1");

        byte[] spsPps = [0x00, 0x00, 0x00, 0x01, 0x67, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x01, 0x68, 0xEE];
        server.OnStreamInfo(
            [new VideoStreamProfile(1920, 1080, spsPps)],
            new AudioFormat(2, 16, 48000, 480),
            isHevc: false);

        var configMsg = await ReceiveTextMatchingAsync(ws, "\"config\"");
        using (var doc = JsonDocument.Parse(configMsg))
        {
            Assert.Equal("config", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("avc1.4d002a", doc.RootElement.GetProperty("codec").GetString());
            Assert.Equal(1920, doc.RootElement.GetProperty("width").GetInt32());
        }

        byte[] delta = [0x00, 0x00, 0x00, 0x01, 0x41, 0x9A];
        server.OnVideoFrame(delta, null);

        byte[] idr = [0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84];
        server.OnVideoFrame(idr, null);

        byte[]? videoPacket = null;
        for (var i = 0; i < 4; i++)
        {
            var (type, data) = await ReceiveMessageAsync(ws);
            if (type == WebSocketMessageType.Binary && data[0] == WebMediaProtocol.VideoFrame)
            {
                videoPacket = data;
                break;
            }
        }

        Assert.NotNull(videoPacket);
        Assert.Equal(WebMediaProtocol.VideoFlagKeyframe, videoPacket![1]);
        Assert.Equal([.. spsPps, .. idr], videoPacket[WebMediaProtocol.MediaHeaderSize..]);

        byte[] opus = [0xAA, 0xBB, 0xCC];
        server.OnAudioFrame(opus);
        var audioPacket = await ReceiveBinaryAsync(ws, WebMediaProtocol.AudioFrame);
        Assert.Equal(opus, audioPacket[WebMediaProtocol.MediaHeaderSize..]);

        await SendButtonsAsync(ws, ControllerButtons.Square, l2: 180);
        var received = await callbacks.WaitForInputAsync(s => s.Buttons == ControllerButtons.Square);
        Assert.Equal(ControllerButtons.Square, received.Buttons);
        Assert.Equal(180, received.L2);

        await ws.SendAsync(Encoding.UTF8.GetBytes("""{"type":"ping","ts":42}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        var pong = await ReceiveTextMatchingAsync(ws, "\"pong\"");
        Assert.Contains("\"ts\":42", pong, StringComparison.Ordinal);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task WebSocket_MultiClientRoom_EnforcesSingleControllerAndSupportsPassControl()
    {
        var callbacks = new FakeCallbacks();
        await using var server = new WebShareServer(callbacks, token: "room-tok");
        callbacks.Server = server;

        using var wsPlayer1 = new ClientWebSocket();
        await wsPlayer1.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws?t={server.Token}"), CancellationToken.None);
        var player1Id = await JoinAsync(wsPlayer1, "Player1");

        using var wsPlayer2 = new ClientWebSocket();
        await wsPlayer2.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws?t={server.Token}"), CancellationToken.None);
        var player2Id = await JoinAsync(wsPlayer2, "Player2");

        Assert.Equal(player1Id, server.ActiveControllerId);
        Assert.Equal("Player1", server.ActiveControllerName);
        Assert.Equal(2, server.GetRoomMembers().Count);

        await SendButtonsAsync(wsPlayer2, ControllerButtons.Circle);
        await Task.Delay(80);
        Assert.DoesNotContain(callbacks.Inputs, s => s.Buttons == ControllerButtons.Circle);

        await SendButtonsAsync(wsPlayer1, ControllerButtons.Cross);
        await callbacks.WaitForInputAsync(s => s.Buttons == ControllerButtons.Cross);

        var passJson = JsonSerializer.Serialize(new { type = "passControl", targetId = player2Id });
        await wsPlayer1.SendAsync(Encoding.UTF8.GetBytes(passJson), WebSocketMessageType.Text, true, CancellationToken.None);

        var updated = await ReceiveTextMatchingAsync(wsPlayer2, $"\"controllerClientId\":\"{player2Id}\"");
        Assert.Equal(player2Id, server.ActiveControllerId);
        Assert.Equal("Player2", server.ActiveControllerName);
        Assert.Contains("\"hasControl\":true", updated, StringComparison.Ordinal);

        await SendButtonsAsync(wsPlayer2, ControllerButtons.Triangle);
        await callbacks.WaitForInputAsync(s => s.Buttons == ControllerButtons.Triangle);

        await SendButtonsAsync(wsPlayer1, ControllerButtons.Options);
        await Task.Delay(80);
        Assert.DoesNotContain(callbacks.Inputs, s => s.Buttons == ControllerButtons.Options);

        await wsPlayer2.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await ReceiveTextMatchingAsync(wsPlayer1, $"\"controllerClientId\":\"{player1Id}\"");
        Assert.Equal(player1Id, server.ActiveControllerId);

        await wsPlayer1.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task WaitingRoomAndDailyPasswordAndAdminApi_EnforceSecurityAndApprovals()
    {
        const string adminKey = "owner-super-secret-key-24";
        const string psPass = "PSSECRET";
        var security = new WebShareSecurityConfig(
            RequireApproval: true,
            RequireDailyPassword: true,
            LockPsButton: true,
            LockToCurrentGame: true,
            AllowRemoteRestMode: true,
            MaxClients: 3,
            AdminKey: adminKey,
            PsButtonUnlockPassword: psPass,
            DailyPasswordEpoch: 0);

        var callbacks = new FakeCallbacks();
        await using var server = new WebShareServer(callbacks, token: "guest-tok", security: security);
        callbacks.Server = server;

        using var http = new HttpClient();

        // Admin routes must reject guest token
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"http://127.0.0.1:{server.Port}/admin?t=guest-tok")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"http://127.0.0.1:{server.Port}/admin/api/state?k=wrong")).StatusCode);

        // Admin page & state succeed with admin key
        var adminHtml = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/admin?k={adminKey}");
        Assert.Contains("Cord Admin", adminHtml, StringComparison.Ordinal);
        Assert.Contains(WebShareServer.AppVersion, adminHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("{{CORD_VERSION}}", adminHtml, StringComparison.Ordinal);

        // Wrong daily password is rejected cleanly
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws?t={server.Token}"), CancellationToken.None);
        await ws.SendAsync(Encoding.UTF8.GetBytes("""{"type":"join","name":"Marcos","password":"BADPWD"}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        var joinErr = await ReceiveTextMatchingAsync(ws, "\"joinError\"");
        Assert.Contains("Senha do dia incorreta", joinErr, StringComparison.Ordinal);

        // Correct daily password puts client in Pending Waiting Room
        var todayCode = DailyPasswordGenerator.ComputeToday(adminKey, 0);
        var joinJson = JsonSerializer.Serialize(new { type = "join", name = "Marcos", password = todayCode });
        await ws.SendAsync(Encoding.UTF8.GetBytes(joinJson), WebSocketMessageType.Text, true, CancellationToken.None);
        var pendingMsg = await ReceiveTextMatchingAsync(ws, "\"pending\"");
        using var pendingDoc = JsonDocument.Parse(pendingMsg);
        var clientId = pendingDoc.RootElement.GetProperty("clientId").GetString()!;

        Assert.Equal(1, server.PendingCount);
        Assert.Equal(0, server.ClientCount);

        // Pending client input MUST be dropped
        await SendButtonsAsync(ws, ControllerButtons.Cross);
        await Task.Delay(80);
        Assert.DoesNotContain(callbacks.Inputs, s => s.Buttons == ControllerButtons.Cross);

        // Approve client via Admin HTTP API
        var approveResp = await http.PostAsJsonAsync(
            $"http://127.0.0.1:{server.Port}/admin/api/action?k={adminKey}",
            new { action = "approve", targetId = clientId, value = (string?)null });
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);

        var welcomeMsg = await ReceiveTextMatchingAsync(ws, "\"welcome\"");
        Assert.Contains(clientId, welcomeMsg, StringComparison.Ordinal);
        Assert.Equal(1, server.ClientCount);
        Assert.Equal(0, server.PendingCount);

        // Wrong PS unlock fails; right unlock succeeds
        await ws.SendAsync(Encoding.UTF8.GetBytes("""{"type":"unlockPs","password":"WRONG"}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        await ReceiveTextMatchingAsync(ws, "\"psUnlockResult\"");
        Assert.False(server.IsClientPsButtonUnlocked(clientId));

        await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "unlockPs", password = psPass })), WebSocketMessageType.Text, true, CancellationToken.None);
        await ReceiveTextMatchingAsync(ws, "\"psUnlocked\"");
        Assert.True(server.IsClientPsButtonUnlocked(clientId));

        // Regenerate guest token invalidates old link without closing live server
        var newTok = server.RegenerateToken();
        Assert.NotEqual("guest-tok", newTok);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"http://127.0.0.1:{server.Port}/api/status?t=guest-tok")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{server.Port}/api/status?t={newTok}")).StatusCode);

        // Kick client via Admin API
        var kickResp = await http.PostAsJsonAsync(
            $"http://127.0.0.1:{server.Port}/admin/api/action?k={adminKey}",
            new { action = "kick", targetId = clientId, value = (string?)null });
        Assert.Equal(HttpStatusCode.OK, kickResp.StatusCode);
        var kickedMsg = await ReceiveTextMatchingAsync(ws, "\"kicked\"");
        Assert.Contains("desconectado", kickedMsg, StringComparison.Ordinal);
        Assert.Equal(0, server.ClientCount);
    }

    private static async Task<string> JoinAsync(ClientWebSocket ws, string name)
    {
        var json = JsonSerializer.Serialize(new { type = "join", name });
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        var welcome = await ReceiveTextMatchingAsync(ws, "\"welcome\"");
        using var doc = JsonDocument.Parse(welcome);
        return doc.RootElement.GetProperty("clientId").GetString()!;
    }

    private static Task SendButtonsAsync(ClientWebSocket ws, ControllerButtons buttons, byte l2 = 0)
    {
        var input = new byte[WebMediaProtocol.ControllerInputSize];
        input[0] = WebMediaProtocol.ControllerInput;
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(1, 4), (uint)buttons);
        input[5] = l2;
        return ws.SendAsync(input, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
    }

    private static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveMessageAsync(ClientWebSocket ws)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, timeout.Token);
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return (result.MessageType, ms.ToArray());
        }
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket ws)
    {
        while (true)
        {
            var (type, data) = await ReceiveMessageAsync(ws);
            if (type == WebSocketMessageType.Text)
                return Encoding.UTF8.GetString(data);
        }
    }

    private static async Task<string> ReceiveTextMatchingAsync(ClientWebSocket ws, string needle)
    {
        for (var i = 0; i < 16; i++)
        {
            var (type, data) = await ReceiveMessageAsync(ws);
            if (type == WebSocketMessageType.Text)
            {
                var text = Encoding.UTF8.GetString(data);
                if (text.Contains(needle, StringComparison.Ordinal))
                    return text;
            }
        }

        throw new TimeoutException($"No text message contained {needle}");
    }

    private static async Task<byte[]> ReceiveBinaryAsync(ClientWebSocket ws, byte expectedKind)
    {
        for (var i = 0; i < 12; i++)
        {
            var (type, data) = await ReceiveMessageAsync(ws);
            if (type == WebSocketMessageType.Binary && data.Length > 0 && data[0] == expectedKind)
                return data;
        }

        throw new TimeoutException($"No binary packet of kind {expectedKind}");
    }

    private sealed class FakeCallbacks : IWebShareCallbacks
    {
        private readonly Lock _lock = new();
        private readonly List<ControllerState> _inputs = [];
        private readonly List<(bool Overflow, int QueueDepth, int RttMs, int DecodeQueueDepth)> _signals = [];
        private Func<ControllerState, bool>? _waitPredicate;
        private TaskCompletionSource<ControllerState>? _waitTcs;
        private TaskCompletionSource<(bool Overflow, int QueueDepth, int RttMs, int DecodeQueueDepth)>? _signalTcs;

        public WebShareServer? Server { get; set; }

        public WebStreamQualityRequest? LastWakeQuality { get; private set; }

        public int KeyFrameRequests { get; private set; }

        public WebClipSaveResult? NextClipResult { get; set; }

        public IReadOnlyList<(bool Overflow, int QueueDepth, int RttMs, int DecodeQueueDepth)> NetworkSignals
        {
            get
            {
                lock (_lock)
                    return [.. _signals];
            }
        }

        public IReadOnlyList<ControllerState> Inputs
        {
            get
            {
                lock (_lock)
                    return [.. _inputs];
            }
        }

        public WebShareStatus GetStatus() => new(
            "PS5 Sala",
            "Ready",
            WebStreamState.Idle,
            true,
            Server?.ClientCount ?? 0,
            null,
            0,
            0,
            48000,
            2,
            null,
            null,
            0,
            Server?.GetRoomMembers(),
            Server?.ActiveControllerId,
            Server?.ActiveControllerName,
            Server?.Security.LockPsButton ?? false,
            Server?.Security.RequireDailyPassword ?? false,
            Server?.Security.RequireApproval ?? false,
            Server?.Security.AllowRemoteRestMode ?? true);

        public Task RequestWakeAndConnectAsync(WebStreamQualityRequest quality, CancellationToken cancellationToken)
        {
            LastWakeQuality = quality;
            return Task.CompletedTask;
        }

        public Task RequestDisconnectAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void RequestKeyFrame() => KeyFrameRequests++;

        public Task<WebClipSaveResult> RequestSaveClipDetailedAsync(CancellationToken cancellationToken) =>
            Task.FromResult(NextClipResult ?? new WebClipSaveResult(false, null, "Sem frames."));

        public void OnActiveControllerNetworkSignal(bool queueOverflow, int queueDepth, int rttMs, int decodeQueueDepth)
        {
            TaskCompletionSource<(bool, int, int, int)>? sigTcs = null;
            var item = (queueOverflow, queueDepth, rttMs, decodeQueueDepth);
            lock (_lock)
            {
                _signals.Add(item);
                if (_signalTcs is not null)
                {
                    sigTcs = _signalTcs;
                    _signalTcs = null;
                }
            }

            sigTcs?.TrySetResult(item);
        }

        public async Task<(bool Overflow, int QueueDepth, int RttMs, int DecodeQueueDepth)> WaitForNetworkSignalAsync()
        {
            TaskCompletionSource<(bool, int, int, int)> tcs;
            lock (_lock)
            {
                if (_signals.Count > 0)
                    return _signals[^1];
                _signalTcs = tcs = new TaskCompletionSource<(bool, int, int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void OnRemoteControllerState(in ControllerState state)
        {
            TaskCompletionSource<ControllerState>? match = null;
            lock (_lock)
            {
                _inputs.Add(state);
                if (_waitPredicate is not null && _waitTcs is not null && _waitPredicate(state))
                {
                    match = _waitTcs;
                    _waitPredicate = null;
                    _waitTcs = null;
                }
            }

            match?.TrySetResult(state);
        }

        public async Task<ControllerState> WaitForInputAsync(Func<ControllerState, bool> predicate)
        {
            TaskCompletionSource<ControllerState> tcs;
            lock (_lock)
            {
                var existing = _inputs.FirstOrDefault(predicate);
                if (!existing.Equals(default) && predicate(existing))
                    return existing;
                _waitPredicate = predicate;
                _waitTcs = tcs = new TaskCompletionSource<ControllerState>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
