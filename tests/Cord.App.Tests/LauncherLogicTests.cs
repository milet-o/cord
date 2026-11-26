using System.Net;
using Cord.App.Ui;
using Cord.Core.Consoles;
using Cord.Core.Settings;
using Cord.Core.Storage;
using Cord.Protocol;
using Cord.Protocol.Discovery;

namespace Cord.App.Tests;

public sealed class LauncherLogicTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cord-app-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void WakeAndPlay_WakesThenLaunchesWhenTheConsoleIsReady()
    {
        var wake = new WakeAndPlay(_time);
        wake.Begin("PS5");

        Assert.Equal(WakeStep.SendWakeup, wake.Update(Entry(ConsoleState.Standby)));
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(WakeStep.Wait, wake.Update(Entry(ConsoleState.Standby)));
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(WakeStep.Launch, wake.Update(Entry(ConsoleState.Ready)));
        Assert.Null(wake.HostId);
        Assert.Equal(WakeStep.Wait, wake.Update(Entry(ConsoleState.Ready)));
    }

    [Fact]
    public void WakeAndPlay_ResendsTheWakeupAndGivesUpAfterTheTimeout()
    {
        var wake = new WakeAndPlay(_time);
        wake.Begin("PS5");
        var sent = 0;

        var step = WakeStep.Wait;
        while (step != WakeStep.TimedOut)
        {
            step = wake.Update(Entry(ConsoleState.Standby));
            if (step == WakeStep.SendWakeup)
                sent++;
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal((int)(WakeAndPlay.Timeout / WakeAndPlay.ResendInterval), sent);
        Assert.Null(wake.HostId);
    }

    [Fact]
    public void WakeAndPlay_WaitsWithoutAnAddressAndStopsWhenCancelled()
    {
        var wake = new WakeAndPlay(_time);
        wake.Begin("PS5");

        Assert.Equal(WakeStep.Wait, wake.Update(null));
        wake.Cancel();
        Assert.Equal(WakeStep.Wait, wake.Update(Entry(ConsoleState.Ready)));
    }

    [Theory]
    [InlineData(ConsoleState.Ready, true, false, "Ready", "Play")]
    [InlineData(ConsoleState.Standby, true, false, "Resting", "WakeAndPlay")]
    [InlineData(ConsoleState.Standby, true, true, "Working", "Waking")]
    [InlineData(ConsoleState.Unknown, true, false, "Offline", "Unavailable")]
    [InlineData(ConsoleState.Ready, false, false, "Offline", "Unavailable")]
    public void ConsoleStatus_PicksTheMainAction(ConsoleState state, bool hasAddress, bool waking, string tone, string action)
    {
        var status = ConsoleStatus.For(Entry(state) with { Address = hasAddress ? IPAddress.Loopback : null }, waking);

        Assert.Equal(Enum.Parse<StatusTone>(tone), status.Tone);
        Assert.Equal(Enum.Parse<ConsoleAction>(action), status.Action);
    }

    [Fact]
    public void ConsoleStatus_ShowsTheRunningGame()
    {
        Assert.Equal("Astro Bot", ConsoleStatus.For(Entry(ConsoleState.Ready) with { RunningApp = "Astro Bot" }, waking: false).Detail);
    }

    [Fact]
    public void SetupFlow_RequiresSignInAndLetsPairingBeSkipped()
    {
        var flow = new SetupFlow();
        Assert.True(flow.CanContinue(signedIn: false, hasPairedConsole: false));
        flow.Back();
        Assert.Equal(SetupStep.Welcome, flow.Step);

        flow.Next();
        Assert.False(flow.CanContinue(signedIn: false, hasPairedConsole: false));
        Assert.True(flow.CanContinue(signedIn: true, hasPairedConsole: false));
        Assert.False(flow.CanSkip);

        flow.Next();
        Assert.Equal(SetupStep.Pair, flow.Step);
        Assert.False(flow.CanContinue(signedIn: true, hasPairedConsole: false));
        Assert.True(flow.CanSkip);
        Assert.True(flow.CanContinue(signedIn: true, hasPairedConsole: true));

        flow.Next();
        flow.Next();
        Assert.Equal(SetupStep.Done, flow.Step);
        flow.Next();
        flow.Back();
        Assert.Equal(SetupStep.Done, flow.Step);
        Assert.False(flow.CanContinue(signedIn: true, hasPairedConsole: true));
    }

    [Fact]
    public async Task AppServices_NeedsSetupUntilCompletedOrAlreadyConfigured()
    {
        var paths = new AppPaths(_directory);
        await using (var fresh = new AppServices(paths))
        {
            Assert.True(fresh.NeedsGuide);
            Assert.True(fresh.NeedsSetup);
            fresh.Settings.Save(fresh.Settings.Current with { GuideCompleted = true });
            Assert.False(fresh.NeedsGuide);
            Assert.True(fresh.NeedsSetup);
            fresh.Account.Save(PsnAccountId.FromUserId(1));
            Assert.True(fresh.NeedsSetup);
            fresh.Consoles.Save(new StoredConsole("PS5", "PS5", null, "00", "", "", 0));
            Assert.False(fresh.NeedsSetup);
            fresh.Consoles.Remove("PS5");
            fresh.Settings.Save(fresh.Settings.Current with { SetupCompleted = true });
        }

        await using var reloaded = new AppServices(paths);
        Assert.False(reloaded.NeedsSetup);
        Assert.Equal(QualityPreset.Balanced, QualityPresets.Match(reloaded.Settings.Current.Quality));
    }

    [Fact]
    public void ManualSearch_ReportsWhatAnsweredAtTheAddress()
    {
        var search = new ManualSearch(_time);
        var relay = IPAddress.Parse("26.50.127.143");
        var found = new ConsoleEntry("PS5", "PS5-TEST", relay, ConsoleState.Ready, false, null, _time.GetUtcNow());

        Assert.Equal(ManualSearchState.Idle, search.Update([]).State);
        search.Begin(relay);
        Assert.Equal(ManualSearchState.Searching, search.Update([Entry(ConsoleState.Ready)]).State);
        Assert.Equal((ManualSearchState.Found, found), search.Update([found]));
        Assert.Equal(ManualSearchState.AlreadyPaired, search.Update([found with { Registered = true }]).State);
        Assert.Equal(ManualSearchState.Searching, search.Update([found with { State = ConsoleState.Unknown, Registered = true }]).State);
    }

    [Fact]
    public void ManualSearch_GivesUpWhenNothingAnswers()
    {
        var search = new ManualSearch(_time);
        search.Begin(IPAddress.Parse("26.50.127.143"));

        _time.Advance(ManualSearch.Timeout - TimeSpan.FromSeconds(1));
        Assert.Equal(ManualSearchState.Searching, search.Update([]).State);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ManualSearchState.NoAnswer, search.Update([]).State);
    }

    [Fact]
    public void SharingMessage_TellsTheFriendWhichAddressToEnter()
    {
        var message = HomeScreen.SharingMessage("Living Room", [IPAddress.Parse("26.50.127.143"), IPAddress.Parse("100.109.163.35")], 1);

        Assert.Contains("\n26.50.127.143  or  100.109.163.35\n", message);
        Assert.StartsWith("Sharing Living Room. 1 computer is connected.\n", message);
    }

    [Fact]
    public void SharingMessage_AsksForAVpnWhenThisComputerIsOnlyOnTheHomeNetwork()
    {
        var message = HomeScreen.SharingMessage("Living Room", [], 0);

        Assert.Contains("VPN", message);
        Assert.EndsWith("No one is connected yet.", message);
    }

    [Fact]
    public void KeyboardInput_ResolvesCustomBindingsAndClampsMouseAxis()
    {
        var custom = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Cross"] = nameof(Hexa.NET.SDL3.SDLScancode.F),
            ["PS"] = "InvalidKeyName",
        };

        var resolved = Input.KeyboardInput.ResolveBindings(custom);

        Assert.Equal(Hexa.NET.SDL3.SDLScancode.F, resolved["Cross"]);
        Assert.Equal(Hexa.NET.SDL3.SDLScancode.P, resolved["PS"]);
        Assert.Equal(short.MaxValue, Input.KeyboardInput.FloatToAxis(2.5f));
        Assert.Equal(short.MinValue, Input.KeyboardInput.FloatToAxis(-2.5f));
    }

    private ConsoleEntry Entry(ConsoleState state) =>
        new("PS5", "Living Room", IPAddress.Loopback, state, true, null, _time.GetUtcNow());

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
