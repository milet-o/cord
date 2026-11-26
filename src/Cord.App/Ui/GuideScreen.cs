using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;

namespace Cord.App.Ui;

/// <summary>5-page interactive quick-start guide shown on first launch and accessible anytime from the top bar.</summary>
internal sealed class GuideScreen
{
    public const int TotalPages = 5;

    private static readonly string[] StepNames =
    [
        "PS5 Setup",
        "Account & Pair",
        "Play Modes",
        "Best Settings",
        "Telegram & Tips",
    ];

    private readonly AppServices _services;

    public GuideScreen(AppServices services) => _services = services;

    public int PageIndex { get; private set; }

    public event Action? Completed;

    public event Action? OpenSettingsRequested;

    public void Open(int page = 0) => PageIndex = Math.Clamp(page, 0, TotalPages - 1);

    public void Draw()
    {
        HandleKeyboardNavigation();

        Widgets.BeginColumn("guide", 880);
        Widgets.Space(16);
        DrawHeader();
        Widgets.Steps(StepNames, PageIndex);
        DrawPageSelector();
        Widgets.Space(10);

        Widgets.BeginCard("guide-body");
        switch (PageIndex)
        {
            case 0:
                DrawPs5SetupPage();
                break;
            case 1:
                DrawAccountAndPairPage();
                break;
            case 2:
                DrawPlayModesPage();
                break;
            case 3:
                DrawBestSettingsPage();
                break;
            default:
                DrawTelegramAndTipsPage();
                break;
        }

        Widgets.EndCard();
        Widgets.Space(14);
        DrawFooter();
        Widgets.Space(28);
        Widgets.EndColumn();
    }

    private void DrawHeader()
    {
        if (!_services.NeedsSetup)
        {
            if (Widgets.IconButton(Icons.ArrowLeft, "guide-back", "Back to your consoles", 44))
                Finish();
            ImGui.SameLine(0, Theme.Dp(12));
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Theme.Dp(2));
        }

        Widgets.Title("Cord Quick-Start Guide");
        var subtitle = $"Page {(PageIndex + 1).ToString(CultureInfo.InvariantCulture)} of {TotalPages.ToString(CultureInfo.InvariantCulture)}  ·  {StepNames[PageIndex]}";
        Widgets.Muted(subtitle, Theme.SmallSize);
        Widgets.Space(10);
    }

    private void DrawPageSelector()
    {
        var gap = Theme.Dp(6);
        var avail = ImGui.GetContentRegionAvail().X;
        var slotWidth = MathF.Max(Theme.Dp(90), (avail - gap * (TotalPages - 1)) / TotalPages);
        ImGui.PushID("guide-tabs");
        for (var i = 0; i < TotalPages; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, gap);
            var label = $"{(i + 1).ToString(CultureInfo.InvariantCulture)}. {StepNames[i]}";
            if (Widgets.Button(label, i == PageIndex ? ButtonKind.Primary : ButtonKind.Secondary, slotWidth))
                PageIndex = i;
        }

        ImGui.PopID();
    }

    private void DrawPs5SetupPage()
    {
        Widgets.Heading("1. Prepare your PS5 & local network");
        Widgets.Muted("Complete these quick settings on your PlayStation 5 console once.");
        Widgets.Space(8);

        Widgets.NumberedStep(1, "On your PS5, open Settings > System > Remote Play and turn on Enable Remote Play.");
        Widgets.NumberedStep(2, "To wake your PS5 from Rest Mode automatically, open Settings > System > Power Saving > Features Available in Rest Mode and enable both 'Stay Connected to the Internet' and 'Enable Turning on PS5 from Network'.");
        Widgets.NumberedStep(3, "Connect both your PS5 and this PC to the same home router. Wired Ethernet on the PS5 is strongly recommended for 1-2 ms latency and zero frame drops.");
        Widgets.Space(10);

        Widgets.BeginCard("privacy-note");
        Widgets.Text($"{Icons.CircleCheck}  100% Local & Private Storage", color: Theme.Success, bold: true);
        Widgets.Space(2);
        Widgets.Muted($"Your PlayStation Network tokens, paired console keys, and optional Telegram Bot credentials stay strictly on this PC ({_services.Paths.DataDirectory}), protected by Windows DPAPI encryption. Nothing is ever sent to any third-party Cord server.", Theme.SmallSize);
        Widgets.EndCard();
    }

    private static void DrawAccountAndPairPage()
    {
        Widgets.Heading("2. Sign in to PSN & pair your console");
        Widgets.Muted("Cord signs in through PlayStation's official website and pairs locally over your LAN.");
        Widgets.Space(8);

        Widgets.NumberedStep(1, "Click 'Sign in with PlayStation Network' in Setup or Settings. PlayStation's official login page opens in your browser — Cord never sees your password.");
        Widgets.NumberedStep(2, "After signing in, the browser page goes blank or shows 'redirect'. That is expected! Copy the entire address bar URL (Ctrl+L, then Ctrl+C) and Cord reads it automatically from your clipboard.");
        Widgets.NumberedStep(3, "On your PS5, go to Settings > System > Remote Play > Link Device to display an 8-digit pairing code.");
        Widgets.NumberedStep(4, "Select your PS5 in Cord (or expand 'Can't find your console?' and enter its LAN IP from PS5 Settings > Network > Connection Status), type the 8-digit PIN, and click Pair.");
        Widgets.Space(8);

        Widgets.Text($"{Icons.Sparkles}  Tip: You only pair once per console.", Theme.SmallSize, Theme.AccentText, bold: true);
        Widgets.Muted("Once paired, your PS5 shows up on the Home screen and wakes automatically when you click Play or Audio.", Theme.SmallSize);
    }

    private static void DrawPlayModesPage()
    {
        Widgets.Heading("3. Play modes & launcher overview");
        Widgets.Muted("Each console card on the Home screen gives you three instant ways to use your PS5:");
        Widgets.Space(8);

        ModeRow(
            Icons.Play,
            Theme.AccentText,
            "Play (Full Native Stream)",
            "Low-latency Direct3D 11 video (up to 1080p 60 fps HEVC/HDR), Opus audio, DualSense haptics & adaptive triggers, 250 Hz controller polling, AMD CAS sharpening, and GPU Frame Generation (F7). Press F1 in-game for stats and options.");

        ModeRow(
            Icons.Headphones,
            Theme.Success,
            "Audio (Audio + Pad Companion — 500 Hz)",
            "Perfect when playing directly on your TV/monitor via HDMI while still hearing PS5 game audio on your PC (with music or Discord) and keeping 500 Hz / 2 ms DualSense responsiveness. Decodes zero video (~1 Mbps LAN) and keeps working silently in the tray at 0% GPU.");

        ModeRow(
            Icons.ExternalLink,
            Theme.Warning,
            "Web (Web Share for Friends & Phones)",
            "Creates a browser playable room with an automatic free *.trycloudflare.com link (or your own Cloudflare Named Tunnel), Waiting Room approval with IP/city info, daily rotating password, PS-button lock, game-exit auto-lock, and a mobile Admin panel.");

        Widgets.Space(4);
        Widgets.Text("More places to explore:", bold: true);
        Widgets.Muted("• Card menu (⋮): Rest Mode, VPN Console Sharing (relay ports to a friend over Tailscale/Radmin), or Forget Console.", Theme.SmallSize);
        Widgets.Muted("• Top bar: Switch Obsidian Dark / PS1 Light theme, reopen this Guide, view live GitHub Patch Notes on the version badge, or open Settings.", Theme.SmallSize);
    }

    private void DrawBestSettingsPage()
    {
        Widgets.Heading("4. Recommended settings & Frame Generation");
        Widgets.Muted("Use these presets for the sharpest picture and lowest input lag:");
        Widgets.Space(8);

        SettingCard(
            "Wired Ethernet (Recommended PC Stream)",
            "1080p · 60 fps · HEVC · 30–50 Mbps (or Balanced 15 Mbps)  |  Hardware Decoding: ON  |  Low-Latency Mode (250 Hz): ON  |  AMD CAS Sharpening: 30–40%",
            Theme.AccentText);

        SettingCard(
            "Wi-Fi or Web Share in Browser",
            "720p · 60 fps · H.264 · 10 Mbps (or 1080p HEVC 10–15 Mbps if supported)  |  Adaptive Bitrate handles brief Wi-Fi drops smoothly.",
            Theme.Success);

        SettingCard(
            "Frame Generation (F7) & VSync",
            "• Low-Latency Extrapolation: synthesizes frames without adding an extra frame queue.\n"
            + "• Smooth Interpolation: turns 30 fps games into silky 60+ fps (+1 frame delay).\n"
            + "• VSync: keep VSync ON when using Frame Generation for even frame pacing. Only turn VSync OFF on VRR/G-Sync monitors when Frame Gen is Off.",
            Theme.Warning);

        if (!_services.NeedsSetup)
        {
            Widgets.Space(6);
            if (Widgets.Button($"{Icons.Settings}  Open Settings now", ButtonKind.Secondary))
            {
                MarkGuideCompleted();
                OpenSettingsRequested?.Invoke();
            }
        }
    }

    private static void DrawTelegramAndTipsPage()
    {
        Widgets.Heading("5. Optional Telegram Bot, shortcuts & live updates");
        Widgets.Muted("Get instant phone alerts and remote-control commands when sharing your PS5 via Web Share.");
        Widgets.Space(8);

        Widgets.NumberedStep(1, "In Telegram, open @BotFather, send /newbot, pick a name, and copy the Bot Token.");
        Widgets.NumberedStep(2, "Open @userinfobot and copy your numeric Chat ID (Cord accepts commands strictly from your own Chat ID).");
        Widgets.NumberedStep(3, "Paste both into Settings > Web Share Security & Mobile Control and click 'Test Telegram message'. Use /status, /link, /novolink, /senha, /travar, /liberar, /pslock, /kick, or /repouso anytime.");
        Widgets.Space(8);

        Widgets.Text("In-game keyboard shortcuts:", bold: true);
        Widgets.Muted("F1 Stream menu & latency HUD   ·   F7 Cycle Frame Generation   ·   F8 Mouse as Right Stick   ·   F9 Save 30s Replay Clip   ·   F11 Fullscreen", Theme.SmallSize);
        Widgets.Space(6);
        Widgets.Text("Automatic GitHub updates & live Patch Notes:", bold: true);
        Widgets.Muted("Click the version pill next to the Cord logo anytime to view live GitHub Patch Notes. Whenever a new release is published on GitHub, Cord alerts you with a changelog popup and one-click update.", Theme.SmallSize);
    }

    private void DrawFooter()
    {
        if (PageIndex > 0)
        {
            if (Widgets.Button($"{Icons.ArrowLeft}  Previous", ButtonKind.Ghost))
                PageIndex--;
        }
        else if (Widgets.Button("Skip guide", ButtonKind.Ghost))
        {
            Finish();
        }

        var isLast = PageIndex == TotalPages - 1;
        var nextLabel = isLast
            ? (_services.NeedsSetup ? $"Start setup  {Icons.ChevronRight}" : $"{Icons.Check}  Done")
            : $"Next  {Icons.ChevronRight}";
        var nextWidth = MathF.Max(Theme.Dp(170), Widgets.ButtonWidth(nextLabel));
        Widgets.SameLineRight(nextWidth);
        if (Widgets.Button(nextLabel, ButtonKind.Primary, nextWidth))
        {
            if (isLast)
                Finish();
            else
                PageIndex++;
        }
    }

    private void HandleKeyboardNavigation()
    {
        if (ImGui.IsAnyItemActive() || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId))
            return;
        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow, false) && PageIndex < TotalPages - 1)
            PageIndex++;
        else if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow, false) && PageIndex > 0)
            PageIndex--;
    }

    private void Finish()
    {
        MarkGuideCompleted();
        Completed?.Invoke();
    }

    private void MarkGuideCompleted()
    {
        if (!_services.Settings.Current.GuideCompleted)
            _services.Settings.Save(_services.Settings.Current with { GuideCompleted = true });
    }

    private static void ModeRow(string icon, Vector4 color, string title, string description)
    {
        Widgets.Text(icon, color: color);
        ImGui.SameLine(0, Theme.Dp(10));
        Widgets.Text(title, bold: true);
        Widgets.Muted(description, Theme.SmallSize);
        Widgets.Space(6);
    }

    private static void SettingCard(string title, string detail, Vector4 accentColor)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.SurfaceRaised);
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.WithAlpha(accentColor, 0.45f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Dp(14, 12));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Theme.Dp(8));
        ImGui.BeginChild(title, new Vector2(Widgets.Fill, 0), ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysUseWindowPadding, ImGuiWindowFlags.NoScrollbar);
        Widgets.Text(title, color: accentColor, bold: true);
        Widgets.Space(2);
        Widgets.Muted(detail, Theme.SmallSize);
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
        Widgets.Space(8);
    }
}
