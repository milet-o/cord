using System.Numerics;
using Hexa.NET.ImGui;
using Cord.App.Streaming;
using Cord.Core.Consoles;

namespace Cord.App.Ui;

internal enum Page
{
    Guide,
    Setup,
    Home,
    Settings,
    Pair,
    Changelog,
}

/// <summary>The window chrome (background, top bar) and navigation between the launcher pages.</summary>
internal sealed class Shell : IDisposable
{
    private readonly AppServices _services;
    private readonly SignInPanel _signIn;
    private readonly PairingPanel _pairing;
    private readonly GuideScreen _guide;
    private readonly SetupScreen _setup;
    private readonly HomeScreen _home;
    private readonly SettingsScreen _settings;
    private readonly PairScreen _pair;
    private readonly ChangelogScreen _changelog;
    private readonly UpdatePopup _updatePopup;
    private StreamController? _reportedStream;

    public Shell(AppServices services)
    {
        _services = services;
        _signIn = new SignInPanel(services);
        _pairing = new PairingPanel(services);
        var forget = new ForgetConsoleDialog(services);
        _guide = new GuideScreen(services);
        _setup = new SetupScreen(services, _signIn, _pairing);
        _home = new HomeScreen(services, forget);
        _settings = new SettingsScreen(services, _signIn, forget);
        _pair = new PairScreen(services, _signIn, _pairing);
        _changelog = new ChangelogScreen(services);
        _updatePopup = new UpdatePopup(services);

        _guide.Completed += () => Page = services.NeedsSetup ? Page.Setup : Page.Home;
        _guide.OpenSettingsRequested += () => Page = Page.Settings;
        _setup.Finished += () => Page = Page.Home;
        _home.PlayRequested += (entry, console) => PlayRequested?.Invoke(entry, console);
        _home.AudioPadRequested += (entry, console) => AudioPadRequested?.Invoke(entry, console);
        _home.AudioPadStopRequested += () => AudioPadStopRequested?.Invoke();
        _home.PairRequested += OpenPairing;
        _home.HideToTrayRequested += () => HideToTrayRequested?.Invoke();
        _settings.BackRequested += () => Page = Page.Home;
        _settings.PairRequested += OpenPairing;
        _settings.ChangelogRequested += () => Page = Page.Changelog;
        _settings.GuideRequested += () => OpenGuide(0);
        _settings.SetupRequested += () =>
        {
            _setup.Restart();
            Page = Page.Setup;
        };
        _pair.BackRequested += () => Page = Page.Home;
        _changelog.BackRequested += () => Page = Page.Home;
        _changelog.UpdatePopupRequested += () => _updatePopup.ForceShow();
        _updatePopup.ExitForUpdateRequested += () => ExitForUpdateRequested?.Invoke();
        Page = services.NeedsGuide ? Page.Guide : services.NeedsSetup ? Page.Setup : Page.Home;
    }

    public event Action<ConsoleEntry, StoredConsole>? PlayRequested;

    public event Action<ConsoleEntry, StoredConsole>? AudioPadRequested;

    public event Action? AudioPadStopRequested;

    public event Action? HideToTrayRequested;

    public event Action? ExitForUpdateRequested;

    public Page Page { get; private set; }

    public void Dispose() => _signIn.Dispose();

    public void Draw(StreamController? lastStream, StreamController? audioStream = null, bool gamepadConnected = false, string? gamepadName = null)
    {
        ReportStreamEnd(lastStream);
        ReportStreamEnd(audioStream);
        var viewport = ImGui.GetMainViewport();
        DrawBackground(viewport);

        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.Begin("Cord", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoBringToFrontOnFocus);
        ImGui.PopStyleVar();

        if (Page is not (Page.Setup or Page.Guide))
            DrawTopBar();

        var padX = viewport.WorkSize.X < Theme.Dp(900) ? Theme.Dp(16) : Theme.Dp(28);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padX, 0));
        ImGui.BeginChild("content", Vector2.Zero, ImGuiChildFlags.AlwaysUseWindowPadding, ImGuiWindowFlags.NoBackground);
        ImGui.PopStyleVar();
        HandleBackShortcut();
        switch (Page)
        {
            case Page.Guide:
                _guide.Draw();
                break;
            case Page.Setup:
                _setup.Draw();
                break;
            case Page.Settings:
                _settings.Draw();
                break;
            case Page.Pair:
                _pair.Draw();
                break;
            case Page.Changelog:
                _changelog.Draw();
                break;
            default:
                _home.Draw(audioStream, gamepadConnected, gamepadName);
                break;
        }

        ImGui.EndChild();
        _updatePopup.Draw();
        ImGui.End();
    }

    private void OpenGuide(int page = 0)
    {
        _guide.Open(page);
        Page = Page.Guide;
    }

    private void OpenPairing()
    {
        _pairing.Reset();
        Page = Page.Pair;
    }

    private void ReportStreamEnd(StreamController? stream)
    {
        if (stream is not { State: StreamState.Ended, Message: { } message } || ReferenceEquals(stream, _reportedStream))
            return;
        _reportedStream = stream;
        _home.ShowNotice(message);
    }

    /// <summary>Escape or the gamepad's Circle button goes back from the secondary pages.</summary>
    private void HandleBackShortcut()
    {
        var allowGuideBack = Page == Page.Guide && !_services.NeedsSetup;
        if ((Page is not (Page.Settings or Page.Pair or Page.Changelog) && !allowGuideBack) || ImGui.IsAnyItemActive() || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId))
            return;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false) || ImGui.IsKeyPressed(ImGuiKey.GamepadFaceRight, false))
            Page = Page.Home;
    }

    private void DrawTopBar()
    {
        var height = Theme.Dp(64);
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetWindowPos();
        var width = ImGui.GetWindowWidth();
        drawList.AddRectFilled(origin, origin + new Vector2(width, height), Theme.U32(Theme.BackgroundTop));
        drawList.AddLine(origin + new Vector2(0, height), origin + new Vector2(width, height), Theme.U32(Theme.Border));

        var middle = height / 2;
        ImGui.SetCursorPos(new Vector2(Theme.Dp(28), middle - Theme.Dp(15)));
        Logo.Draw(30);
        var brandX = Theme.Dp(28 + 30 + 10);
        ImGui.PushFont(Theme.SemiBold, 20);
        var brandSize = ImGui.CalcTextSize("Cord");
        ImGui.SetCursorPos(new Vector2(brandX, middle - brandSize.Y / 2));
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Text);
        ImGui.TextUnformatted("Cord");
        ImGui.PopStyleColor();
        ImGui.PopFont();

        var hasUpdate = _services.Updates.AvailableUpdate is not null;
        var versionText = AppVersion.Display;
        ImGui.PushFont(Theme.SemiBold, Theme.SmallSize - 1);
        var badgeTextSize = ImGui.CalcTextSize(versionText);
        ImGui.PopFont();
        var badgePad = Theme.Dp(7, 2);
        var badgeSize = badgeTextSize + badgePad * 2;
        var badgePos = origin + new Vector2(brandX + brandSize.X + Theme.Dp(10), middle - badgeSize.Y / 2);
        ImGui.SetCursorScreenPos(badgePos);
        if (ImGui.InvisibleButton("version-badge", badgeSize))
            Page = Page == Page.Changelog ? Page.Home : Page.Changelog;
        var badgeHovered = ImGui.IsItemHovered();
        ImGui.SetItemTooltip(hasUpdate ? "Update available! Click for patch notes" : "View patch notes & update history");
        drawList.AddRectFilled(badgePos, badgePos + badgeSize, Theme.U32(badgeHovered || Page == Page.Changelog ? Theme.SurfaceHover : Theme.Surface), Theme.Dp(5));
        drawList.AddRect(badgePos, badgePos + badgeSize, Theme.U32(hasUpdate ? Theme.Success : badgeHovered || Page == Page.Changelog ? Theme.BorderStrong : Theme.Border), Theme.Dp(5));
        Widgets.DrawCentered(drawList, Theme.SemiBold, Theme.SmallSize - 1, badgePos + badgeSize / 2, Page == Page.Changelog ? Theme.AccentText : Theme.TextMuted, versionText);
        if (hasUpdate)
            drawList.AddCircleFilled(new Vector2(badgePos.X + badgeSize.X - Theme.Dp(2), badgePos.Y + Theme.Dp(2)), Theme.Dp(4), Theme.U32(Theme.Success));

        var settingsX = width - Theme.Dp(28 + 40);
        var accountLabel = _services.Account.AccountId is null ? $"{Icons.User}  Sign in" : $"{Icons.CircleCheck}  Signed in";
        var accountWidth = Widgets.ButtonWidth(accountLabel);
        var accountX = settingsX - accountWidth - Theme.Dp(6);

        var themeLabel = Theme.IsLight ? "PS1 Light" : "Obsidian";
        var themeWidth = Widgets.ButtonWidth(themeLabel);
        var themeX = accountX - themeWidth - Theme.Dp(6);

        var guideLabel = $"{Icons.Info}  Guide";
        var guideWidth = Widgets.ButtonWidth(guideLabel);
        var guideX = themeX - guideWidth - Theme.Dp(6);

        ImGui.SetCursorPos(new Vector2(guideX, middle - Theme.Dp(20)));
        if (Widgets.Button(guideLabel, ButtonKind.Ghost))
            OpenGuide(0);
        ImGui.SetItemTooltip("Open the 5-page Cord Quick-Start Guide");

        ImGui.SetCursorPos(new Vector2(themeX, middle - Theme.Dp(20)));
        if (Widgets.Button(themeLabel, ButtonKind.Ghost))
        {
            var nextLight = !Theme.IsLight;
            _services.Settings.Save(_services.Settings.Current with { LightTheme = nextLight });
            Theme.Apply(Theme.CurrentScale, nextLight);
        }

        ImGui.SetItemTooltip("Switch between Obsidian Dark and PS1 Concrete Light");

        ImGui.SetCursorPos(new Vector2(accountX, middle - Theme.Dp(20)));
        if (Widgets.Button(accountLabel, ButtonKind.Ghost))
            Page = Page.Settings;
        ImGui.SetItemTooltip(_services.Account.AccountId is null ? "Sign in to PlayStation Network" : "PlayStation Network account");

        ImGui.SetCursorPos(new Vector2(settingsX, middle - Theme.Dp(20)));
        if (Widgets.IconButton(Icons.Settings, "settings", "Settings", 40))
            Page = Page == Page.Settings ? Page.Home : Page.Settings;
        ImGui.SetCursorPos(new Vector2(0, height + 1));
    }

    private static void DrawBackground(ImGuiViewportPtr viewport)
    {
        var drawList = ImGui.GetBackgroundDrawList(viewport);
        drawList.AddRectFilled(viewport.Pos, viewport.Pos + viewport.Size, Theme.U32(Theme.Background));
    }
}
