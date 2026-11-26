using System.Net;
using System.Numerics;
using Hexa.NET.ImGui;
using Cord.App.Ui;
using Cord.Core.Consoles;
using Cord.Core.Storage;
using Cord.Protocol;
using Cord.Protocol.Discovery;

namespace Cord.App.Tests;

/// <summary>Draws every launcher page with a real (headless) Dear ImGui context to catch layout and id-stack errors.</summary>
public sealed unsafe class LauncherRenderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cord-ui-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ImGuiContextPtr _context;
    private readonly AppServices _services;

    public LauncherRenderTests()
    {
        _context = ImGui.CreateContext();
        ImGui.SetCurrentContext(_context);
        var io = ImGui.GetIO();
        io.IniFilename = null;
        io.DisplaySize = new Vector2(1280, 800);
        io.DeltaTime = 1 / 60f;
        // Textures are never uploaded here; the flag just lets the atlas grow on demand.
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
        Theme.LoadFonts(io);
        Theme.Apply(1.5f);
        _services = new AppServices(new AppPaths(_directory));
    }

    public void Dispose()
    {
        _services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        ImGui.DestroyContext(_context);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void FirstRunStartsWithTheGuideThenSetup()
    {
        using (var firstShell = new Shell(_services))
        {
            Assert.Equal(Page.Guide, firstShell.Page);
            Assert.True(Render(() => firstShell.Draw(null)) > 0);
        }

        _services.Settings.Save(_services.Settings.Current with { GuideCompleted = true });
        using var setupShell = new Shell(_services);
        Assert.Equal(Page.Setup, setupShell.Page);
        Assert.True(Render(() => setupShell.Draw(null)) > 0);
    }

    [Fact]
    public void ConfiguredInstallationOpensTheHomeScreenWithItsConsoles()
    {
        _services.Account.Save(PsnAccountId.FromUserId(1));
        _services.Consoles.Save(new StoredConsole("PS5", "Living Room", "192.168.0.10", "00", "", "", 0));
        using var shell = new Shell(_services);

        Assert.Equal(Page.Home, shell.Page);
        var drawn = Render(() => shell.Draw(null));
        Assert.True(drawn > 0);

        _services.Sharing.Share(new ConsoleEntry("PS5", "Living Room", null, ConsoleState.Unknown, true, null, DateTimeOffset.MinValue));
        Assert.NotNull(_services.Sharing.Error);
        _services.WebShare.Start(new ConsoleEntry("PS5", "Living Room", IPAddress.Loopback, ConsoleState.Ready, true, null, DateTimeOffset.UtcNow), startTunnel: false);
        Assert.NotNull(_services.WebShare.LocalUrl);
        Render(() => shell.Draw(null));
        _services.WebShare.Stop();
    }

    [Fact]
    public void SmallViewportWithWebShareDrawsCleanly()
    {
        _services.Account.Save(PsnAccountId.FromUserId(1));
        _services.Consoles.Save(new StoredConsole("PS5", "Living Room", "192.168.0.10", "00", "", "", 0));
        using var shell = new Shell(_services);
        _services.WebShare.Start(new ConsoleEntry("PS5", "Living Room", IPAddress.Loopback, ConsoleState.Ready, true, null, DateTimeOffset.UtcNow), startTunnel: false);
        ImGui.GetIO().DisplaySize = new Vector2(800, 560);
        try
        {
            var drawn = Render(() => shell.Draw(null, gamepadConnected: true, gamepadName: "DualSense Wireless Controller"));
            Assert.True(drawn > 0);
        }
        finally
        {
            ImGui.GetIO().DisplaySize = new Vector2(1280, 800);
            _services.WebShare.Stop();
        }
    }

    [Fact]
    public void EveryPanelAndScreenDraws()
    {
        using var signIn = new SignInPanel(_services);
        var pairing = new PairingPanel(_services);
        var forget = new ForgetConsoleDialog(_services);
        var settings = new SettingsScreen(_services, signIn, forget);
        var changelog = new ChangelogScreen(_services);
        var guide = new GuideScreen(_services);
        var updatePopup = new UpdatePopup(_services);
        var pair = new PairScreen(_services, signIn, pairing);
        var setup = new SetupScreen(_services, signIn, pairing);

        Render(() => InWindow(settings.Draw));
        Render(() => InWindow(changelog.Draw));
        for (var page = 0; page < GuideScreen.TotalPages; page++)
        {
            guide.Open(page);
            Assert.True(Render(() => InWindow(guide.Draw)) > 0);
        }

        Render(() => InWindow(updatePopup.Draw));
        Render(() => InWindow(pair.Draw));
        Render(() => InWindow(setup.Draw));
        Render(() => InWindow(() => QualityPicker.Draw(_services.Settings.Current.Quality)));
        _services.Account.Save(PsnAccountId.FromUserId(1));
        Render(() => InWindow(signIn.Draw));
        forget.Request("PS5", "Living Room");
        Render(() => InWindow(forget.Draw));
    }

    [Fact]
    public void LightThemeRendersCleanlyAcrossAllScreens()
    {
        Theme.Apply(1.5f, light: true);
        Assert.True(Theme.IsLight);
        _services.Account.Save(PsnAccountId.FromUserId(1));
        _services.Consoles.Save(new StoredConsole("PS5", "Sala", "192.168.0.10", "00", "", "", 0));
        using var shell = new Shell(_services);
        Assert.True(Render(() => shell.Draw(null)) > 0);
        Theme.Apply(1.5f, light: false);
        Assert.False(Theme.IsLight);
    }

    private static void InWindow(Action draw)
    {
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(ImGui.GetIO().DisplaySize);
        ImGui.Begin("test", ImGuiWindowFlags.NoDecoration);
        draw();
        ImGui.End();
    }

    /// <summary>Renders a few frames and returns the number of vertices of the last one.</summary>
    private static int Render(Action draw)
    {
        var vertices = 0;
        for (var frame = 0; frame < 3; frame++)
        {
            ImGui.NewFrame();
            draw();
            ImGui.Render();
            vertices = ImGui.GetDrawData().TotalVtxCount;
        }

        return vertices;
    }
}
