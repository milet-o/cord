using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using Hexa.NET.SDL3;
using Cord.App.Input;
using Cord.Core.Settings;
using Cord.Core.Storage;
using Cord.Protocol.Session;
using Cord.WebShare;

namespace Cord.App.Ui;

/// <summary>Quality, display/audio, Web Share security, Telegram Bot, remapping and advanced settings.</summary>
internal sealed class SettingsScreen
{
    private static readonly string[] ResolutionNames = ["360p", "540p", "720p", "1080p"];
    private static readonly string[] FpsNames = ["30 fps", "60 fps"];
    private static readonly int[] FpsValues = [30, 60];
    private static readonly string[] CodecNames = ["H.264", "HEVC", "HEVC HDR"];
    private static readonly string[] FrameGenNames = ["Off", "Low-Latency", "Smooth"];

    private readonly AppServices _services;
    private readonly SignInPanel _signIn;
    private readonly ForgetConsoleDialog _forget;
    private bool _showCustom;
    private bool _showAdvanced;
    private bool _showRemap;
    private string? _listeningActionId;

    private string? _psUnlockDraft;
    private string? _botTokenDraft;
    private string? _chatIdDraft;
    private string? _cfTokenDraft;
    private string? _cfUrlDraft;
    private Task<(bool Ok, string Message)>? _telegramTestTask;
    private string? _telegramTestStatus;
    private bool _telegramTestOk;

    public SettingsScreen(AppServices services, SignInPanel signIn, ForgetConsoleDialog forget)
    {
        _services = services;
        _signIn = signIn;
        _forget = forget;
    }

    public event Action? BackRequested;

    public event Action? PairRequested;

    public event Action? SetupRequested;

    public event Action? ChangelogRequested;

    public event Action? GuideRequested;

    public void Draw()
    {
        PollTelegramTest();

        Widgets.BeginColumn("settings", 860);
        Widgets.Space(20);
        if (Widgets.IconButton(Icons.ArrowLeft, "back", "Back to your consoles", 44))
            BackRequested?.Invoke();
        ImGui.SameLine(0, Theme.Dp(12));
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Theme.Dp(2));
        Widgets.Title("Settings");
        Widgets.Space(8);

        Section("Stream quality", DrawQuality);
        Section("Display & Audio", DrawDisplay);
        Section("Web Share Security & Mobile Control", DrawWebShareSecurity);
        Section("PlayStation Network", _signIn.Draw);
        Section("Consoles", DrawConsoles);
        Section("While playing & Keybindings", DrawControls);
        Section("Advanced", DrawAdvanced);
        if (Widgets.Button($"{Icons.Info}  Quick-start guide", ButtonKind.Ghost))
            GuideRequested?.Invoke();
        ImGui.SameLine();
        if (Widgets.Button($"{Icons.Sparkles}  Patch notes ({AppVersion.Display})", ButtonKind.Ghost))
            ChangelogRequested?.Invoke();
        if (_services.Updates.IsConfigured)
        {
            ImGui.SameLine();
            var checking = _services.Updates.IsChecking;
            if (Widgets.Button(checking ? "Checking GitHub..." : "Check for updates", ButtonKind.Ghost, enabled: !checking))
                _services.Updates.RefreshNow();
        }

        Widgets.Space(4);
        Widgets.Text($"Cord {AppVersion.Display}  ·  Not affiliated with or endorsed by Sony Interactive Entertainment.", Theme.SmallSize, Theme.TextFaint);

        _forget.Draw();
        Widgets.Space(40);
        Widgets.EndColumn();
    }

    private static void Section(string title, Action body)
    {
        Widgets.Text(title, Theme.SmallSize, Theme.TextMuted, bold: true);
        Widgets.BeginCard(title);
        body();
        Widgets.EndCard();
        Widgets.Space(14);
    }

    private void DrawQuality()
    {
        var settings = _services.Settings.Current;
        Widgets.Muted("Higher quality needs a faster network. If the picture stutters, pick a lower setting.");
        Widgets.Space(4);
        if (QualityPicker.Draw(settings.Quality) is { } preset)
            Save(settings with { Quality = preset.ToQuality() });

        var custom = QualityPresets.Match(settings.Quality) is null;
        Widgets.Space(2);
        if (Widgets.Button($"{Icons.Sliders}  {(_showCustom ? "Hide custom settings" : "Customize")}", ButtonKind.Ghost))
            _showCustom = !_showCustom;
        if (custom)
        {
            ImGui.SameLine();
            Widgets.Text("Custom settings in use", Theme.SmallSize, Theme.AccentHover);
        }

        if (_showCustom || custom)
            DrawCustomQuality(settings);
    }

    private void DrawCustomQuality(AppSettings settings)
    {
        var quality = settings.Quality;
        var resolution = (int)quality.Resolution;
        var fps = Math.Max(0, Array.IndexOf(FpsValues, quality.Fps));
        var codec = (int)quality.Codec - 1;
        var automatic = quality.BitrateKbps is null;
        var mbps = quality.EffectiveBitrateKbps / 1000;

        Widgets.Space(4);
        Label("Resolution");
        var changed = Widgets.Segmented("resolution", ResolutionNames, ref resolution);
        Label("Frame rate");
        changed |= Widgets.Segmented("fps", FpsNames, ref fps);
        Label("Video format");
        changed |= Widgets.Segmented("codec", CodecNames, ref codec);
        Widgets.Text("HEVC gives a better picture for the same bandwidth. HDR needs an HDR display.", Theme.SmallSize, Theme.TextFaint);
        Widgets.Space(4);
        changed |= Widgets.Toggle("Automatic bitrate", "Picks a bitrate that suits the resolution.", ref automatic);
        if (!automatic)
        {
            ImGui.SetNextItemWidth(Widgets.Fill);
            changed |= ImGui.SliderInt("##bitrate", ref mbps, 2, 100, "%d Mbps");
        }

        var updated = new StreamQuality((Resolution)resolution, FpsValues[fps], (VideoCodec)(codec + 1), automatic ? null : mbps * 1000);
        Widgets.Text($"Uses up to {updated.EffectiveBitrateKbps / 1000} Mbps of your network.", Theme.SmallSize, Theme.TextMuted);
        if (changed)
        {
            _showCustom = true;
            Save(settings with { Quality = updated });
        }
    }

    private void DrawDisplay()
    {
        var settings = _services.Settings.Current;
        var fullscreen = settings.Fullscreen;
        var vsync = settings.VSync;
        var lowLatency = settings.LowLatencyMode;
        var fgIndex = (int)settings.FrameGen;
        var sharpPercent = (int)MathF.Round(Math.Clamp(settings.Sharpness, 0f, 1f) * 100);
        var hardware = settings.HardwareDecoding;
        var miniHud = settings.ShowMiniHud;
        var minimizeToTray = settings.MinimizeToTray;
        var lightTheme = settings.LightTheme;
        var volPercent = (int)MathF.Round(Math.Clamp(settings.AudioVolume, 0f, 1f) * 100);

        Label($"Audio volume ({volPercent.ToString(CultureInfo.InvariantCulture)}%)");
        ImGui.SetNextItemWidth(Widgets.Fill);
        var volChanged = ImGui.SliderInt("##volume", ref volPercent, 0, 100, "%d%%");

        var themeChanged = Widgets.Toggle("Light theme (PS1 Concrete)", "Warm matte concrete light palette inspired by classic Sony hardware.", ref lightTheme);
        var changed = Widgets.Toggle("Show compact Mini-HUD while playing", "Small top-left overlay with ping, fps, resolution, Mbps, client latency and jitter.", ref miniHud);
        changed |= Widgets.Toggle("Fullscreen while playing", "You can also switch any time with F11.", ref fullscreen);
        changed |= Widgets.Toggle("VSync", "Avoids tearing. Turn it off with a VRR/G-Sync display for the lowest possible delay.", ref vsync);
        changed |= Widgets.Toggle("Low-latency mode (250 Hz input & Flip Model)", "Uses 4 ms controller polling, 1 ms timer precision, and waitable DXGI swapchain.", ref lowLatency);
        changed |= Widgets.Toggle("Hardware decoding", "Uses your graphics card to decode the video. Applies to the next stream.", ref hardware);
        if (OperatingSystem.IsWindows())
            changed |= Widgets.Toggle("Minimize to tray when Web Share is active (0% GPU)", "Closing the window keeps the tunnel, Telegram bot and PS5 access alive silently in the system tray.", ref minimizeToTray);

        Widgets.Space(4);
        Label("Frame Generation (F7, Windows D3D11)");
        changed |= Widgets.Segmented("frame-gen", FrameGenNames, ref fgIndex);
        Widgets.Text("Low-Latency extrapolates without queuing frames; Smooth interpolates 30->60 or 60->120+ fps (+1 frame delay).", Theme.SmallSize, Theme.TextFaint);
        if (fgIndex != (int)FrameGenMode.Off && !vsync)
            Widgets.Text("Warning: Frame Generation works much smoother with VSync enabled.", Theme.SmallSize, Theme.Warning);

        Label($"Sharpening (AMD CAS, {sharpPercent.ToString(CultureInfo.InvariantCulture)}%)");
        ImGui.SetNextItemWidth(Widgets.Fill);
        changed |= ImGui.SliderInt("##sharpness", ref sharpPercent, 0, 100, "%d%%");

        if (changed || volChanged || themeChanged)
        {
            if (themeChanged)
                Theme.Apply(Theme.CurrentScale, lightTheme);

            Save(settings with
            {
                AudioVolume = volPercent / 100f,
                ShowMiniHud = miniHud,
                Fullscreen = fullscreen,
                VSync = vsync,
                LowLatencyMode = lowLatency,
                FrameGen = (FrameGenMode)fgIndex,
                Sharpness = sharpPercent / 100f,
                HardwareDecoding = hardware,
                MinimizeToTray = minimizeToTray,
                LightTheme = lightTheme,
            });
        }
    }

    private void DrawWebShareSecurity()
    {
        var settings = _services.Settings.Current;
        var ws = settings.WebShare;

        _psUnlockDraft ??= SecretText.Unprotect(ws.PsButtonUnlockPasswordProtected) ?? "";
        _botTokenDraft ??= SecretText.Unprotect(ws.TelegramBotTokenProtected) ?? "";
        _chatIdDraft ??= ws.TelegramChatId ?? "";
        _cfTokenDraft ??= SecretText.Unprotect(ws.CloudflareTunnelTokenProtected) ?? "";
        _cfUrlDraft ??= ws.CloudflarePublicUrl ?? "";

        var reqApproval = ws.RequireApproval;
        var reqDaily = ws.RequireDailyPassword;
        var lockPs = ws.LockPsButton;
        var lockGame = ws.LockToCurrentGame;
        var allowRest = ws.AllowRemoteRestMode;
        var maxClients = Math.Clamp(ws.MaxClients, 1, 8);

        var changed = Widgets.Toggle("Require owner approval (Waiting Room)", "Visitors stay on hold with their IP & city/ISP until you approve via Telegram, Phone Admin or PC.", ref reqApproval);
        changed |= Widgets.Toggle("Require daily password", "Visitors must enter today's rotating 6-char code on the join screen.", ref reqDaily);
        changed |= Widgets.Toggle("Lock PS button for friends", "Blocks the PS button so friends cannot leave the game or open PS5 Settings unless they enter the separate PS password.", ref lockPs);
        changed |= Widgets.Toggle("Auto-lock control if game exits", "If the active game closes or changes to PS5 Home, locks remote control automatically and notifies your phone.", ref lockGame);
        changed |= Widgets.Toggle("Allow friends to put PS5 in Rest Mode", "Shows a Rest Mode button for the friend currently holding control.", ref allowRest);

        Label("Maximum friends in room");
        ImGui.SetNextItemWidth(Widgets.Fill);
        changed |= ImGui.SliderInt("##max-clients", ref maxClients, 1, 8, "%d friends");

        var adminKey = _services.Settings.EnsureWebShareAdminKey();
        var todayCode = DailyPasswordGenerator.ComputeToday(adminKey, ws.DailyPasswordEpoch);
        Widgets.Space(2);
        Widgets.Text($"Today's daily password: {todayCode}", Theme.SmallSize, Theme.Warning, bold: true);

        Label("Separate PS button unlock password (optional)");
        ImGui.SetNextItemWidth(Widgets.Fill);
        var secretEdited = ImGui.InputTextWithHint("##ps-unlock", "Leave empty to keep PS button locked to everyone", ref _psUnlockDraft, 64, ImGuiInputTextFlags.Password);

        Widgets.Space(6);
        Widgets.Text("Telegram Bot (Instant Phone Alerts & Control)", bold: true);
        Widgets.Muted("Create a bot with @BotFather for the Token and get your numeric Chat ID with @userinfobot. Only your Chat ID can control the PS5.");

        Label("Telegram Bot Token");
        ImGui.SetNextItemWidth(Widgets.Fill);
        secretEdited |= ImGui.InputTextWithHint("##tg-token", "123456:ABC-...", ref _botTokenDraft, 256, ImGuiInputTextFlags.Password);

        Label("Your Telegram Chat ID");
        ImGui.SetNextItemWidth(Widgets.Fill);
        secretEdited |= ImGui.InputTextWithHint("##tg-chat", "ex: 123456789", ref _chatIdDraft, 64);

        var testing = _telegramTestTask is { IsCompleted: false };
        if (Widgets.Button(testing ? "Sending test..." : "Test Telegram message", ButtonKind.Secondary, enabled: !testing))
        {
            var tokenCopy = _botTokenDraft;
            var chatCopy = _chatIdDraft;
            var adminUrl = _services.WebShare.AdminUrl ?? _services.WebShare.LocalAdminUrl;
            _telegramTestTask = TelegramOwnerBot.SendTestMessageAsync(tokenCopy, chatCopy, adminUrl);
        }

        if (_telegramTestStatus is not null)
        {
            ImGui.SameLine();
            Widgets.Text(_telegramTestStatus, Theme.SmallSize, _telegramTestOk ? Theme.Success : Theme.Danger);
        }

        Widgets.Space(6);
        Widgets.Text("Fixed Public Link (Optional Cloudflare Named Tunnel)", bold: true);
        Widgets.Muted("Leave blank to use automatic free *.trycloudflare.com links.");
        Label("Cloudflare Tunnel Token (optional)");
        ImGui.SetNextItemWidth(Widgets.Fill);
        secretEdited |= ImGui.InputTextWithHint("##cf-token", "eyJhIjoi...", ref _cfTokenDraft, 512, ImGuiInputTextFlags.Password);
        Label("Public URL configured in Cloudflare Zero Trust");
        ImGui.SetNextItemWidth(Widgets.Fill);
        secretEdited |= ImGui.InputTextWithHint("##cf-url", "https://ps5.yourdomain.com", ref _cfUrlDraft, 256);

        if (changed || secretEdited)
        {
            var updatedWs = ws with
            {
                RequireApproval = reqApproval,
                RequireDailyPassword = reqDaily,
                LockPsButton = lockPs,
                LockToCurrentGame = lockGame,
                AllowRemoteRestMode = allowRest,
                MaxClients = maxClients,
                PsButtonUnlockPasswordProtected = SecretText.Protect(_psUnlockDraft),
                TelegramBotTokenProtected = SecretText.Protect(_botTokenDraft),
                TelegramChatId = string.IsNullOrWhiteSpace(_chatIdDraft) ? null : _chatIdDraft.Trim(),
                CloudflareTunnelTokenProtected = SecretText.Protect(_cfTokenDraft),
                CloudflarePublicUrl = string.IsNullOrWhiteSpace(_cfUrlDraft) ? null : _cfUrlDraft.Trim(),
            };
            Save(settings with { WebShare = updatedWs });
            _services.WebShare.RefreshSecurityFromSettings();
        }
    }

    private void PollTelegramTest()
    {
        if (_telegramTestTask is not { IsCompleted: true } task)
            return;

        _telegramTestTask = null;
        try
        {
            var result = task.GetAwaiter().GetResult();
            _telegramTestOk = result.Ok;
            _telegramTestStatus = result.Message;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            _telegramTestOk = false;
            _telegramTestStatus = e.Message;
        }
    }

    private void DrawConsoles()
    {
        var consoles = _services.Consoles.Consoles;
        if (consoles.Count == 0)
            Widgets.Muted("No paired consoles yet.");
        foreach (var console in consoles)
        {
            ImGui.PushID(console.HostId);
            var y = ImGui.GetCursorPosY();
            Widgets.Text($"{Icons.Gamepad}  {console.Nickname}", bold: true);
            Widgets.Text(console.LastAddress ?? "Address unknown", Theme.SmallSize, Theme.TextMuted);
            var label = $"{Icons.Trash}  Forget";
            Widgets.SameLineRight(Widgets.ButtonWidth(label));
            ImGui.SetCursorPosY(y + Theme.Dp(2));
            if (Widgets.Button(label, ButtonKind.Danger))
                _forget.Request(console.HostId, console.Nickname);
            ImGui.PopID();
        }

        if (Widgets.Button($"{Icons.Plus}  Pair a console"))
            PairRequested?.Invoke();
    }

    private void DrawControls()
    {
        var settings = _services.Settings.Current;
        Row("F1", "Show or hide the stream menu (statistics, Web Share and disconnect)");
        Row("F7", "Cycle Frame Generation (Off / Low-Latency Extrapolation / Smooth Interpolation)");
        Row("F8", "Toggle Mouse as Right Stick (relative mouse look)");
        Row("F9", "Save Instant Replay (last 30 seconds to Videos/Cord/*.mp4)");
        Row("F11", "Switch fullscreen");
        Widgets.Space(4);

        var mouseStick = settings.MouseRightStick;
        var sens = settings.MouseSensitivity;
        var mouseChanged = Widgets.Toggle("Use mouse for Right Stick (PC stream)", "Can also be toggled anytime with F8 while streaming.", ref mouseStick);
        if (mouseStick)
        {
            Label("Mouse sensitivity");
            ImGui.SetNextItemWidth(Widgets.Fill);
            mouseChanged |= ImGui.SliderFloat("##mouse-sens", ref sens, 0.2f, 4.0f, "%.2fx");
        }

        if (mouseChanged)
            Save(settings with { MouseRightStick = mouseStick, MouseSensitivity = sens });

        Widgets.Space(4);
        if (Widgets.Button(_showRemap ? "Hide keyboard remapping" : $"{Icons.Sliders}  Customize keyboard controls", ButtonKind.Ghost))
            _showRemap = !_showRemap;

        if (!_showRemap)
            return;

        Widgets.Muted(_listeningActionId is null
            ? "Click any button below and press the key you want to bind (Esc cancels)."
            : "Press any key to bind, or Esc to cancel...");
        Widgets.Space(2);

        if (_listeningActionId is not null)
            CaptureKeyRebind(settings);

        var resolved = KeyboardInput.ResolveBindings(settings.KeyBindings);
        var columns = 2;
        var slotWidth = (ImGui.GetContentRegionAvail().X - Theme.Dp(12)) / columns;
        for (var i = 0; i < KeyboardInput.Actions.Count; i++)
        {
            var action = KeyboardInput.Actions[i];
            if (i % columns != 0)
                ImGui.SameLine(0, Theme.Dp(12));

            ImGui.PushID("bind-" + action.Id);
            var keyName = _listeningActionId == action.Id
                ? "Press a key..."
                : KeyboardInput.ScancodeLabel(resolved[action.Id]);
            var kind = _listeningActionId == action.Id ? ButtonKind.Primary : ButtonKind.Secondary;
            if (Widgets.Button($"{action.Label}: {keyName}", kind, slotWidth))
                _listeningActionId = action.Id;
            ImGui.PopID();
        }

        Widgets.Space(4);
        if (Widgets.Button("Reset keyboard defaults", ButtonKind.Ghost))
        {
            _listeningActionId = null;
            Save(settings with { KeyBindings = null });
        }
    }

    private unsafe void CaptureKeyRebind(AppSettings settings)
    {
        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            _listeningActionId = null;
            return;
        }

        int count;
        var state = SDL.GetKeyboardState(&count);
        for (var i = 4; i < count; i++)
        {
            if (!state[i])
                continue;
            var sc = (SDLScancode)i;
            if (sc is SDLScancode.Escape or SDLScancode.F1 or SDLScancode.F7 or SDLScancode.F8 or SDLScancode.F9 or SDLScancode.F11)
                continue;

            var updated = settings.KeyBindings is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(settings.KeyBindings, StringComparer.Ordinal);
            updated[_listeningActionId!] = sc.ToString();
            _listeningActionId = null;
            Save(settings with { KeyBindings = updated });
            break;
        }
    }

    private void DrawAdvanced()
    {
        if (Widgets.Button(_showAdvanced ? "Hide advanced options" : "Show advanced options", ButtonKind.Ghost))
            _showAdvanced = !_showAdvanced;
        if (!_showAdvanced)
            return;

        var settings = _services.Settings.Current;
        var maxLoss = (float)settings.MaxReportedPacketLoss * 100;
        var ffmpegPath = settings.FfmpegPath ?? "";

        Label("Highest packet loss reported to the console");
        ImGui.SetNextItemWidth(Widgets.Fill);
        var changed = ImGui.SliderFloat("##loss", ref maxLoss, 0, 20, "%.0f %%");
        Widgets.Text("Lower values keep the quality up on unstable networks; higher values let the console lower its bitrate sooner.", Theme.SmallSize, Theme.TextFaint);

        Label("FFmpeg folder (optional, needs a restart)");
        ImGui.SetNextItemWidth(Widgets.Fill);
        changed |= ImGui.InputTextWithHint("##ffmpeg", "Use the bundled FFmpeg", ref ffmpegPath, 512);
        Widgets.Text(_services.FfmpegVersion is { } version ? $"FFmpeg {version} loaded." : "FFmpeg is not loaded.", Theme.SmallSize, Theme.TextFaint);
        Widgets.Text($"Settings are stored in {_services.Paths.DataDirectory}", Theme.SmallSize, Theme.TextFaint);
        Widgets.Space(4);
        if (Widgets.Button("Run the setup again"))
            SetupRequested?.Invoke();

        if (changed)
        {
            Save(settings with
            {
                MaxReportedPacketLoss = maxLoss / 100,
                FfmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? null : ffmpegPath.Trim(),
            });
        }
    }

    private void Save(AppSettings settings) => _services.Settings.Save(settings);

    private static void Label(string text)
    {
        Widgets.Space(2);
        Widgets.Text(text, Theme.SmallSize, Theme.TextMuted, bold: true);
    }

    private static void Row(string key, string description)
    {
        ImGui.PushFont(Theme.SemiBold, Theme.SmallSize);
        var size = ImGui.CalcTextSize(key) + Theme.Dp(16, 6);
        ImGui.PopFont();
        var position = ImGui.GetCursorScreenPos();
        var width = Theme.Dp(52);
        ImGui.GetWindowDrawList().AddRectFilled(position, position + new Vector2(width, size.Y), Theme.U32(Theme.SurfaceRaised), Theme.Dp(6));
        Widgets.DrawCentered(ImGui.GetWindowDrawList(), Theme.SemiBold, Theme.SmallSize, position + new Vector2(width, size.Y) / 2, Theme.Text, key);
        ImGui.Dummy(new Vector2(width, size.Y));
        ImGui.SameLine(0, Theme.Dp(14));
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Theme.Dp(3));
        Widgets.Text(description, Theme.SmallSize, Theme.TextMuted);
    }
}
