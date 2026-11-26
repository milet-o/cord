using System.Net;
using Cord.Core.Consoles;
using Cord.Core.Settings;
using Cord.Core.Storage;
using Cord.Protocol.Crypto;
using Cord.Protocol.Discovery;
using Cord.Protocol.Input;
using Cord.Protocol.Registration;

namespace Cord.WebShare.Tests;

public sealed class ConsoleWebShareTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cord-webshare-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(ConsoleState.Standby, WebStreamState.Waking)]
    [InlineData(ConsoleState.Ready, WebStreamState.Connecting)]
    public async Task RequestWakeAndConnect_TransitionsStateSynchronouslyBeforeReturning(
        ConsoleState consoleState,
        WebStreamState expectedStreamState)
    {
        var paths = new AppPaths(_directory);
        var registry = new ConsoleRegistry(paths);
        registry.Save(StoredConsole.From(
            "PS5-TEST",
            "127.0.0.1",
            new RegisteredConsole("01234567"u8.ToArray(), new byte[16], 0, new byte[6], "PS5-TEST"),
            "PS5-TEST"));

        await using var browser = new ConsoleBrowser(registry);
        using var webShare = new ConsoleWebShare(RpCryptTables.Default, registry, browser);

        webShare.Start(
            new ConsoleEntry("PS5-TEST", "PS5-TEST", IPAddress.Loopback, consoleState, Registered: true, RunningApp: null, DateTimeOffset.UtcNow),
            startTunnel: false);

        var callbacks = (IWebShareCallbacks)webShare;
        await callbacks.RequestWakeAndConnectAsync(WebStreamQualityRequest.Default, CancellationToken.None);

        var status = callbacks.GetStatus();
        Assert.Equal(expectedStreamState, status.StreamState);
        Assert.False(string.IsNullOrWhiteSpace(status.StatusDetail));

        await callbacks.RequestDisconnectAsync(CancellationToken.None);
        Assert.Equal(WebStreamState.Idle, callbacks.GetStatus().StreamState);
    }

    [Fact]
    public async Task DetachLocalStream_StartsPostDisconnectCooldownTimer()
    {
        var paths = new AppPaths(_directory);
        var registry = new ConsoleRegistry(paths);
        await using var browser = new ConsoleBrowser(registry);
        using var webShare = new ConsoleWebShare(RpCryptTables.Default, registry, browser);

        webShare.Start(
            new ConsoleEntry("PS5-TEST", "PS5-TEST", IPAddress.Loopback, ConsoleState.Ready, Registered: true, RunningApp: null, DateTimeOffset.UtcNow),
            startTunnel: false);

        Assert.Equal(0, webShare.CooldownRemainingSeconds);
        webShare.AttachLocalStream("PS5-TEST", () => { });
        webShare.DetachLocalStream();

        Assert.InRange(webShare.CooldownRemainingSeconds, 1, 15);
        Assert.InRange(((IWebShareCallbacks)webShare).GetStatus().CooldownSeconds, 1, 15);
    }

    [Fact]
    public async Task GameExitAutoLock_LocksControllerAfterTwoMismatchedDiscoveryUpdates()
    {
        var paths = new AppPaths(_directory);
        var settings = new SettingsStore(paths);
        settings.Save(settings.Current with
        {
            WebShare = settings.Current.WebShare with { LockToCurrentGame = true },
        });
        var registry = new ConsoleRegistry(paths);
        await using var browser = new ConsoleBrowser(registry);
        using var webShare = new ConsoleWebShare(RpCryptTables.Default, registry, browser, settings);

        var initial = new ConsoleEntry("PS5-TEST", "PS5-TEST", IPAddress.Loopback, ConsoleState.Ready, Registered: true, RunningApp: "Elden Ring", DateTimeOffset.UtcNow);
        webShare.Start(initial, startTunnel: false);
        webShare.AttachLocalStream("PS5-TEST", () => { });

        Assert.Equal("Elden Ring", webShare.LockedGameName);
        Assert.True(webShare.ControllerAllowed);

        // First change is debounced
        webShare.Follow([initial with { RunningApp = null }]);
        Assert.True(webShare.ControllerAllowed);

        // Second consecutive observation away from the game auto-locks control
        webShare.Follow([initial with { RunningApp = null }]);
        Assert.False(webShare.ControllerAllowed);
        Assert.Null(webShare.LockedGameName);
    }

    [Fact]
    public async Task PsButtonLock_StripsPsButtonForRemoteInput()
    {
        var paths = new AppPaths(_directory);
        var settings = new SettingsStore(paths);
        settings.Save(settings.Current with
        {
            WebShare = settings.Current.WebShare with { LockPsButton = true, RequireApproval = false },
        });
        var registry = new ConsoleRegistry(paths);
        await using var browser = new ConsoleBrowser(registry);
        using var webShare = new ConsoleWebShare(RpCryptTables.Default, registry, browser, settings);

        var entry = new ConsoleEntry("PS5-TEST", "PS5-TEST", IPAddress.Loopback, ConsoleState.Ready, Registered: true, RunningApp: "FIFA", DateTimeOffset.UtcNow);
        webShare.Start(entry, startTunnel: false);

        using var ws = new System.Net.WebSockets.ClientWebSocket();
        await ws.ConnectAsync(new Uri(webShare.LocalUrl!.Replace("http://", "ws://", StringComparison.Ordinal).Replace("/?", "/ws?", StringComparison.Ordinal)), CancellationToken.None);
        var join = """{"type":"join","name":"Lucas"}"""u8.ToArray();
        await ws.SendAsync(join, System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        await Task.Delay(100);

        ((IWebShareCallbacks)webShare).OnRemoteControllerState(
            ControllerState.Idle with { Buttons = ControllerButtons.Cross | ControllerButtons.PS },
            "fake-client");

        Assert.True(webShare.TryGetRemoteInput(out var captured));
        Assert.True(captured.Buttons.HasFlag(ControllerButtons.Cross));
        Assert.False(captured.Buttons.HasFlag(ControllerButtons.PS));
    }

    [Fact]
    public async Task RequestStop_ClearsHostIdImmediatelyWithoutBlocking()
    {
        var paths = new AppPaths(_directory);
        var settings = new SettingsStore(paths);
        var registry = new ConsoleRegistry(paths);
        await using var browser = new ConsoleBrowser(registry);
        using var webShare = new ConsoleWebShare(RpCryptTables.Default, registry, browser, settings);

        var entry = new ConsoleEntry("PS5-TEST", "PS5-TEST", IPAddress.Loopback, ConsoleState.Ready, Registered: true, RunningApp: null, DateTimeOffset.UtcNow);
        webShare.Start(entry, startTunnel: false);
        Assert.Equal("PS5-TEST", webShare.HostId);
        Assert.NotNull(webShare.LocalUrl);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        webShare.RequestStop();
        sw.Stop();

        Assert.Null(webShare.HostId);
        Assert.Null(webShare.LocalUrl);
        Assert.True(sw.ElapsedMilliseconds < 250);
    }

    [Fact]
    public void AdaptiveBitrateController_RampsOnCongestionAndDecaysWhenSmooth()
    {
        var controller = new AdaptiveBitrateController();
        Assert.Equal(0.0, controller.CurrentLossRatio);
        Assert.Equal(0, controller.PressurePercent);

        // Clean baseline observation establishes baseline RTT
        var loss = controller.Observe(nowMs: 1000, queueOverflow: false, queueDepth: 0, rttMs: 20, decodeQueueDepth: 0);
        Assert.Equal(0.0, loss);

        // Mild decodeQueueDepth <= 2 without queue overflow or RTT spike does not trigger throttling
        loss = controller.Observe(nowMs: 1200, queueOverflow: false, queueDepth: 1, rttMs: 22, decodeQueueDepth: 2);
        Assert.Equal(0.0, loss);

        // RTT spike triggers moderate congestion
        loss = controller.Observe(nowMs: 1500, queueOverflow: false, queueDepth: 1, rttMs: 140, decodeQueueDepth: 0);
        Assert.True(loss >= 0.025);

        // Queue overflow ramps further and clamps at MaxLossRatio (0.10)
        for (var i = 0; i < 6; i++)
            loss = controller.Observe(nowMs: 2000 + (i * 200), queueOverflow: true, queueDepth: 6, rttMs: 140, decodeQueueDepth: 6);
        Assert.Equal(AdaptiveBitrateController.MaxLossRatio, loss);
        Assert.Equal(100, controller.PressurePercent);

        // Clean observations wait during 1.5s hold then decay smoothly down toward 0
        var beforeDecay = controller.CurrentLossRatio;
        var afterShortClean = controller.Observe(nowMs: 3600, queueOverflow: false, queueDepth: 0, rttMs: 22, decodeQueueDepth: 0);
        Assert.Equal(beforeDecay, afterShortClean);

        for (var t = 4600; t <= 12000; t += 450)
            loss = controller.Observe(nowMs: t, queueOverflow: false, queueDepth: 0, rttMs: 22, decodeQueueDepth: 0);
        Assert.True(loss < beforeDecay);
        Assert.Equal(0.0, loss);

        controller.Observe(nowMs: 13000, queueOverflow: true, queueDepth: 6, rttMs: 200, decodeQueueDepth: 6);
        Assert.True(controller.CurrentLossRatio > 0);
        controller.Reset();
        Assert.Equal(0.0, controller.CurrentLossRatio);
        Assert.Equal(0, controller.PressurePercent);
    }
}
