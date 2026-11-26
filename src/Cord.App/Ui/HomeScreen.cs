using System.Globalization;
using System.Net;
using System.Numerics;
using Hexa.NET.ImGui;
using Cord.App.Platform;
using Cord.App.Streaming;
using Cord.Core.Consoles;
using Cord.Protocol.Discovery;

namespace Cord.App.Ui;

/// <summary>The launcher: adaptive console cards plus Web Share and Audio + Pad status banners.</summary>
internal sealed class HomeScreen
{
    private readonly AppServices _services;
    private readonly ForgetConsoleDialog _forget;
    private readonly WakeAndPlay _wake = new();
    private readonly BackgroundJob _wakeJob = new();
    private readonly BackgroundJob _restJob = new();
    private string? _notice;
    private long _webLinkCopiedUntil;
    private bool _wakeForAudioOnly;

    public HomeScreen(AppServices services, ForgetConsoleDialog forget)
    {
        _services = services;
        _forget = forget;
    }

    public event Action<ConsoleEntry, StoredConsole>? PlayRequested;

    public event Action<ConsoleEntry, StoredConsole>? AudioPadRequested;

    public event Action? AudioPadStopRequested;

    public event Action? PairRequested;

    public event Action? HideToTrayRequested;

    public void ShowNotice(string message) => _notice = message;

    public void Draw(StreamController? audioStream = null, bool gamepadConnected = false, string? gamepadName = null)
    {
        var entries = _services.Browser.Snapshot();
        UpdateWake(entries);
        _services.Sharing.Follow(entries);
        _services.WebShare.Follow(entries);

        Widgets.BeginColumn("home", 1120);
        Widgets.Space(14);
        var paired = entries.Where(e => e.Registered).ToList();
        Widgets.Title("Your consoles");
        Widgets.Muted(paired.Count == 0 ? "Pair your PS5 to get started." : "Pick a console to start playing.");
        Widgets.Space(4);
        DrawBanners(entries, audioStream, gamepadConnected, gamepadName);

        if (paired.Count == 0)
            DrawEmptyState();
        else
            DrawGrid(paired, audioStream);

        _forget.Draw();
        Widgets.Space(20);
        Widgets.EndColumn();
    }

    private void DrawBanners(IReadOnlyList<ConsoleEntry> entries, StreamController? audioStream, bool gamepadConnected, string? gamepadName)
    {
        foreach (var problem in _services.StartupProblems)
            Widgets.Banner(problem, Icons.CircleAlert, problem, Theme.Danger);
        if (_notice is not null && Widgets.Banner("notice", Icons.Info, _notice, Theme.TextMuted, "Dismiss"))
            _notice = null;
        if (_wakeJob.Error is { } error && Widgets.Banner("wake-error", Icons.CircleAlert, $"Could not wake the console: {error}", Theme.Danger, "Dismiss"))
            _wakeJob.ClearError();
        if (_restJob.Error is { } restError && Widgets.Banner("rest-error", Icons.CircleAlert, $"Rest mode error: {restError}", Theme.Danger, "Dismiss"))
            _restJob.ClearError();
        DrawAudioPadBanner(audioStream, gamepadConnected, gamepadName);
        DrawSharingBanners(entries);
        DrawWebShareBanners(entries);

        var found = entries.FirstOrDefault(e => !e.Registered && e.State == ConsoleState.Ready);
        if (found is not null && Widgets.Banner("found", Icons.Sparkles, $"New PS5 found on your network: {found.Name}", Theme.Accent, "Pair"))
            PairRequested?.Invoke();
    }

    private void DrawAudioPadBanner(StreamController? audioStream, bool gamepadConnected, string? gamepadName)
    {
        if (audioStream is not { AudioOnly: true, State: StreamState.Connecting or StreamState.Streaming, IsStopping: false })
            return;

        var name = audioStream.ConsoleName ?? audioStream.HostId ?? "PS5";
        var stateLabel = audioStream.State == StreamState.Connecting ? "Connecting..." : $"{audioStream.RttMs.ToString(CultureInfo.InvariantCulture)} ms · 500 Hz input · 40 ms audio";

        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Surface);
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.Success);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Dp(18, 14));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Theme.Dp(10));
        ImGui.BeginChild("audio-pad-banner", new Vector2(Widgets.Fill, 0), ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysUseWindowPadding, ImGuiWindowFlags.NoScrollbar);

        Widgets.Text($"{Icons.Headphones}  Audio + Pad active · {name} · {stateLabel}", color: Theme.Success, bold: true);
        if (gamepadConnected)
            Widgets.Text($"Controller: {gamepadName ?? "Gamepad"} (500 Hz background events active)", Theme.SmallSize, Theme.TextMuted);
        else
            Widgets.Text("No gamepad detected — connect DualSense/controller via USB or Bluetooth.", Theme.SmallSize, Theme.Warning);

        var volPercent = (int)MathF.Round(Math.Clamp(_services.Settings.Current.AudioVolume, 0f, 1f) * 100);
        ImGui.SetNextItemWidth(Theme.Dp(200));
        if (ImGui.SliderInt("##audio-pad-vol", ref volPercent, 0, 100, "Volume: %d%%"))
        {
            var vol = volPercent / 100f;
            audioStream.SetAudioVolume(vol);
            _services.Settings.Save(_services.Settings.Current with { AudioVolume = vol });
        }

        if (OperatingSystem.IsWindows())
        {
            SameLineOrWrap(Widgets.ButtonWidth("Run in tray (0% GPU)"));
            if (Widgets.Button("Run in tray (0% GPU)##audio-tray"))
                HideToTrayRequested?.Invoke();
        }

        SameLineOrWrap(Widgets.ButtonWidth("Stop"));
        if (Widgets.Button("Stop##audio-stop", ButtonKind.Danger))
            AudioPadStopRequested?.Invoke();

        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    private void DrawSharingBanners(IReadOnlyList<ConsoleEntry> entries)
    {
        var sharing = _services.Sharing;
        if (sharing.Error is { } error && Widgets.Banner("share-error", Icons.CircleAlert, $"Could not share the console: {error}", Theme.Danger, "Dismiss"))
            sharing.ClearError();
        if (sharing.HostId is not { } hostId)
            return;

        var name = entries.FirstOrDefault(e => e.HostId == hostId)?.Name ?? hostId;
        if (Widgets.Banner("sharing", Icons.Share, SharingMessage(name, sharing.ShareAddresses, sharing.ClientCount), Theme.Accent, "Stop sharing"))
            sharing.Stop();
    }

    internal static string SharingMessage(string name, IReadOnlyList<IPAddress> addresses, int clients)
    {
        var connected = clients switch
        {
            0 => "No one is connected yet.",
            1 => "1 computer is connected.",
            _ => $"{clients} computers are connected.",
        };
        if (addresses.Count == 0)
            return $"Sharing {name}, but this computer is only on your home network. Connect it and your friend's computer to the same VPN (for example Radmin VPN, Tailscale or ZeroTier). {connected}";

        return $"Sharing {name}. {connected}\n"
            + "On your friend's computer, choose \"Can't find your console?\" when pairing in Cord and enter:\n"
            + string.Join("  or  ", addresses) + "\n"
            + "Then open Pair Device on the PS5 and tell them the code.";
    }

    private void DrawWebShareBanners(IReadOnlyList<ConsoleEntry> entries)
    {
        var webShare = _services.WebShare;
        if (webShare.Error is { } error && Widgets.Banner("web-share-error", Icons.CircleAlert, $"Web link: {error}", Theme.Danger, "Dismiss"))
            webShare.ClearError();
        if (webShare.HostId is not { } hostId)
            return;

        var entry = entries.FirstOrDefault(e => e.HostId == hostId);
        var name = entry?.Name ?? hostId;
        var clients = webShare.ClientCount;
        var maxClients = Math.Clamp(_services.Settings.Current.WebShare.MaxClients, 1, 8);
        var clientText = $"{clients.ToString(CultureInfo.InvariantCulture)}/{maxClients.ToString(CultureInfo.InvariantCulture)} online";
        var playerText = webShare.ActiveControllerName is { } player ? $"  ·  Control: {player}" : "";
        var uploadText = webShare.EstimatedUploadMbps > 0 ? $"  ·  ~{webShare.EstimatedUploadMbps:0.0} Mbps up" : "";
        var telegramText = webShare.TelegramActive ? "  ·  Telegram ON" : "";

        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Surface);
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.Accent);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Dp(18, 16));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Theme.Dp(10));
        ImGui.BeginChild("web-share-banner", new Vector2(Widgets.Fill, 0), ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysUseWindowPadding, ImGuiWindowFlags.NoScrollbar);

        Widgets.Text($"{Icons.ExternalLink}  Web Share · {name}  ·  {clientText}{playerText}{uploadText}{telegramText}", color: Theme.AccentText, bold: true);

        if (webShare.ShareUrl is { } shareUrl)
            Widgets.Text($"Guest: {shareUrl}", Theme.SmallSize);
        else if (webShare.TunnelStarting)
            Widgets.Progress("Starting Cloudflare public link...");
        else if (webShare.LocalUrl is { } localUrl)
            Widgets.Text($"Local link: {localUrl}", Theme.SmallSize, Theme.TextMuted);

        if (webShare.DailyPassword is { } pass && _services.Settings.Current.WebShare.RequireDailyPassword)
            Widgets.Text($"Daily password: {pass}", Theme.SmallSize, Theme.Warning, bold: true);

        if (webShare.RunningApp is { } app)
        {
            var lockInfo = webShare.LockedGameName is { } lg ? $" (Auto-lock active on {lg})" : "";
            Widgets.Text($"Running: {app}{lockInfo}", Theme.SmallSize, Theme.TextMuted);
        }

        // Pending access requests
        var pending = webShare.PendingClients;
        if (pending.Count > 0)
        {
            Widgets.Space(4);
            Widgets.Text($"Pending approval ({pending.Count.ToString(CultureInfo.InvariantCulture)}):", Theme.SmallSize, Theme.Warning, bold: true);
            foreach (var p in pending)
            {
                ImGui.PushID("pend-" + p.Id);
                Widgets.Text($"  {p.Name}  ·  {p.Ip} ({p.Location})", Theme.SmallSize);
                ImGui.SameLine();
                if (Widgets.Button($"{Icons.Check} Accept", ButtonKind.Primary))
                {
                    if (!webShare.ApproveClient(p.Id, out var err) && err is not null)
                        _notice = err;
                }

                ImGui.SameLine();
                if (Widgets.Button($"{Icons.X} Deny", ButtonKind.Danger))
                    webShare.DenyClient(p.Id);
                ImGui.PopID();
            }
        }

        // Connected members
        var members = webShare.Members;
        if (members.Count > 0)
        {
            Widgets.Space(4);
            Widgets.Text("Room members:", Theme.SmallSize, Theme.TextMuted, bold: true);
            foreach (var m in members)
            {
                ImGui.PushID("mem-" + m.Id);
                var role = m.HasControl ? " [Controller]" : "";
                var psState = m.PsButtonUnlocked ? " [PS unlocked]" : "";
                Widgets.Text($"  {m.Name}{role}{psState}  ·  {m.Ip ?? "-"} ({m.Location ?? ""})", Theme.SmallSize);
                if (!m.HasControl)
                {
                    ImGui.SameLine();
                    if (Widgets.Button("Pass control"))
                        webShare.AssignControl(m.Id);
                }

                ImGui.SameLine();
                if (Widgets.Button("Kick", ButtonKind.Danger))
                    webShare.KickClient(m.Id);
                ImGui.PopID();
            }
        }

        Widgets.Space(6);
        var urlToCopy = webShare.ShareUrl ?? webShare.LocalUrl;
        if (urlToCopy is not null)
        {
            var copied = Environment.TickCount64 < _webLinkCopiedUntil;
            var copyLabel = copied ? $"{Icons.Check}  Copied!##copy-guest" : $"{Icons.Clipboard}  Copy guest link##copy-guest";
            if (Widgets.Button(copyLabel, ButtonKind.Primary))
            {
                SdlHost.SetClipboardText(urlToCopy);
                _webLinkCopiedUntil = Environment.TickCount64 + 2000;
            }

            SameLineOrWrap(Widgets.ButtonWidth($"{Icons.ExternalLink}  Open"));
            if (Widgets.Button($"{Icons.ExternalLink}  Open##open-guest"))
                SdlHost.OpenUrl(urlToCopy);
            ImGui.SetItemTooltip("Open the guest player page in your browser.");
        }

        var adminUrl = webShare.AdminUrl ?? webShare.LocalAdminUrl;
        if (adminUrl is not null)
        {
            SameLineOrWrap(Widgets.ButtonWidth($"{Icons.User}  Open admin"));
            if (Widgets.Button($"{Icons.User}  Open admin##open-admin"))
                SdlHost.OpenUrl(adminUrl);

            ImGui.SetItemTooltip("Open the admin manager page in your browser to approve requests, kick players, or put PS5 to rest.");
        }

        SameLineOrWrap(Widgets.ButtonWidth("New link"));
        if (Widgets.Button("New link"))
            webShare.RegenerateGuestToken();
        if (OperatingSystem.IsWindows())
        {
            SameLineOrWrap(Widgets.ButtonWidth("Run in tray (0% GPU)"));
            if (Widgets.Button("Run in tray (0% GPU)"))
                HideToTrayRequested?.Invoke();
        }

        SameLineOrWrap(Widgets.ButtonWidth("Stop"));
        if (Widgets.Button("Stop", ButtonKind.Danger))
            webShare.RequestStop();

        Widgets.Space(2);
        if (Widgets.Button(webShare.ControllerAllowed ? "Control: ON" : "Control: OFF"))
            webShare.ControllerAllowed = !webShare.ControllerAllowed;

        SameLineOrWrap(Widgets.ButtonWidth(webShare.PsButtonLocked ? "PS Btn: LOCKED" : "PS Btn: FREE"));
        if (Widgets.Button(webShare.PsButtonLocked ? "PS Btn: LOCKED" : "PS Btn: FREE"))
            webShare.PsButtonLocked = !webShare.PsButtonLocked;

        if (entry?.State == ConsoleState.Ready)
        {
            SameLineOrWrap(Widgets.ButtonWidth($"{Icons.Power} Rest PS5"));
            if (Widgets.Button($"{Icons.Power} Rest PS5"))
                _restJob.Run(() => webShare.PutConsoleToRestAsync());
        }

        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    private void DrawEmptyState()
    {
        Widgets.Space(30);
        Widgets.BeginCard("empty");
        Widgets.Space(16);
        Widgets.Centered(Icons.Gamepad, Theme.HeroSize, Theme.TextMuted);
        Widgets.Space(6);
        Widgets.Centered("No consoles yet", Theme.HeadingSize, bold: true);
        Widgets.Centered("Pair your PS5 once and it shows up here.", color: Theme.TextMuted);
        Widgets.Space(14);
        var width = Theme.Dp(240);
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - width) / 2);
        if (Widgets.Button($"{Icons.Plus}  Pair a console", ButtonKind.Primary, width))
            PairRequested?.Invoke();
        Widgets.Space(16);
        Widgets.EndCard();
    }

    private void DrawGrid(List<ConsoleEntry> paired, StreamController? audioStream)
    {
        var avail = ImGui.GetContentRegionAvail();
        var spacing = Theme.Dp(16);
        var columns = Math.Max(1, (int)((avail.X + spacing) / (Theme.Dp(320) + spacing)));
        var cardW = MathF.Min(Theme.Dp(380), MathF.Max(Theme.Dp(280), (avail.X - spacing * (columns - 1)) / columns));
        var cardH = Math.Clamp(avail.Y - Theme.Dp(20), Theme.Dp(235), Theme.Dp(300));
        var size = new Vector2(cardW, cardH);

        for (var i = 0; i <= paired.Count; i++)
        {
            if (i % columns != 0)
                ImGui.SameLine(0, spacing);
            else if (i > 0)
                ImGui.Dummy(new Vector2(0, spacing - ImGui.GetStyle().ItemSpacing.Y));

            if (i < paired.Count)
            {
                var stored = _services.Consoles.Find(paired[i].HostId);
                if (stored is not null)
                    DrawCard(paired[i], stored, size, audioStream);
            }
            else if (Widgets.Tile("add-console", Icons.Plus, "Pair a console", size))
            {
                PairRequested?.Invoke();
            }
        }
    }

    private void DrawCard(ConsoleEntry entry, StoredConsole stored, Vector2 size, StreamController? audioStream)
    {
        var status = ConsoleStatus.For(entry, _wake.HostId == entry.HostId);
        var tone = ToneColor(status.Tone);
        var artH = Math.Clamp(size.Y - Theme.Dp(168), Theme.Dp(64), Theme.Dp(126));

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Dp(16, 16));
        ImGui.BeginChild(entry.HostId, size, ImGuiChildFlags.AlwaysUseWindowPadding, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();

        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetWindowPos();
        var artMin = origin + Theme.Dp(10, 10);
        var artMax = new Vector2(origin.X + size.X - Theme.Dp(10), origin.Y + artH);
        drawList.AddRectFilled(artMin, artMax, Theme.U32(Theme.SurfaceRaised), Theme.Dp(8));
        drawList.AddRect(artMin, artMax, Theme.U32(Theme.Border), Theme.Dp(8));
        var artCenter = (artMin + artMax) / 2;
        var iconColor = entry.State == ConsoleState.Ready ? Theme.Text : Theme.TextMuted;
        Widgets.DrawCentered(drawList, Theme.Regular, artH < Theme.Dp(85) ? 32 : 44, artCenter, iconColor, Icons.Gamepad);

        ImGui.SetCursorPos(new Vector2(size.X - Theme.Dp(54), Theme.Dp(14)));
        if (Widgets.IconButton(Icons.EllipsisVertical, "menu", "More options"))
            ImGui.OpenPopup("console-menu");
        DrawMenu(entry, stored, audioStream);

        ImGui.SetCursorPosY(artH + Theme.Dp(18));
        Widgets.Text(entry.Name, Theme.HeadingSize - 1, bold: true);
        Widgets.Dot(tone);
        ImGui.SameLine(0, Theme.Dp(8));
        Widgets.Text(status.Label, Theme.SmallSize + 1, tone, bold: true);
        if (status.Detail is not null)
            Widgets.Text(status.Detail, Theme.SmallSize, Theme.TextMuted);

        ImGui.SetCursorPosY(size.Y - Theme.Dp(18 + 40));
        DrawAction(entry, stored, status.Action, audioStream);
        ImGui.EndChild();
    }

    private void DrawMenu(ConsoleEntry entry, StoredConsole stored, StreamController? audioStream)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Dp(8, 8));
        if (ImGui.BeginPopup("console-menu"))
        {
            var isAudioThis = IsAudioActiveFor(audioStream, entry.HostId);
            if (isAudioThis)
            {
                if (ImGui.Selectable($"{Icons.X}  Stop Audio + Pad"))
                    AudioPadStopRequested?.Invoke();
            }
            else if (ImGui.Selectable($"{Icons.Headphones}  Audio + Pad only (no video)", false, entry.Address is null ? ImGuiSelectableFlags.Disabled : ImGuiSelectableFlags.None))
            {
                StartOrWakeAudioPad(entry, stored);
            }

            var webShare = _services.WebShare;
            if (webShare.HostId == entry.HostId)
            {
                if (ImGui.Selectable($"{Icons.ExternalLink}  Stop web link"))
                    webShare.RequestStop();
            }
            else if (ImGui.Selectable($"{Icons.ExternalLink}  Share web link (browser)", false, entry.Address is null ? ImGuiSelectableFlags.Disabled : ImGuiSelectableFlags.None))
            {
                webShare.Start(entry);
            }

            if (ImGui.Selectable($"{Icons.Power}  Put PS5 to Rest Mode", false, entry.State == ConsoleState.Ready && entry.Address is not null ? ImGuiSelectableFlags.None : ImGuiSelectableFlags.Disabled))
            {
                var addr = entry.Address!;
                var creds = stored.ToCredentials();
                _restJob.Run(() => ConsoleWebShare.PutConsoleToRestStandaloneAsync(_services.Tables, addr, creds));
                _notice = $"Sent Rest Mode command to {entry.Name}.";
            }

            var sharing = _services.Sharing;
            if (sharing.HostId == entry.HostId)
            {
                if (ImGui.Selectable($"{Icons.Share}  Stop sharing"))
                    sharing.Stop();
            }
            else if (ImGui.Selectable($"{Icons.Share}  Share with a friend", false, entry.Address is null ? ImGuiSelectableFlags.Disabled : ImGuiSelectableFlags.None))
            {
                sharing.Share(entry);
            }

            if (ImGui.Selectable($"{Icons.Trash}  Forget this console"))
                _forget.Request(entry.HostId, entry.Name);
            ImGui.EndPopup();
        }

        ImGui.PopStyleVar();
    }

    private void DrawAction(ConsoleEntry entry, StoredConsole stored, ConsoleAction action, StreamController? audioStream)
    {
        var webShare = _services.WebShare;
        var isSharingThis = string.Equals(webShare.HostId, entry.HostId, StringComparison.OrdinalIgnoreCase);
        var isAudioThis = IsAudioActiveFor(audioStream, entry.HostId);
        var gap = Theme.Dp(6);
        var webWidth = Theme.Dp(78);
        var audioWidth = Theme.Dp(94);
        var mainWidth = MathF.Max(Theme.Dp(86), ImGui.GetContentRegionAvail().X - webWidth - audioWidth - gap * 2);

        switch (action)
        {
            case ConsoleAction.Play:
                if (Widgets.Button($"{Icons.Play} Play", ButtonKind.Primary, mainWidth))
                {
                    _wakeForAudioOnly = false;
                    PlayRequested?.Invoke(entry, stored);
                }

                break;
            case ConsoleAction.WakeAndPlay:
                if (Widgets.Button($"{Icons.Power} Wake", ButtonKind.Primary, mainWidth))
                {
                    _wakeForAudioOnly = false;
                    _wake.Begin(entry.HostId);
                }

                ImGui.SetItemTooltip("Turn on PS5 and play");
                break;
            case ConsoleAction.Waking:
                if (Widgets.Button("Cancel", ButtonKind.Secondary, mainWidth))
                {
                    _wakeForAudioOnly = false;
                    _wake.Cancel();
                }

                break;
            default:
                Widgets.Button("Offline", ButtonKind.Secondary, mainWidth, enabled: false);
                break;
        }

        ImGui.SameLine(0, gap);
        if (isAudioThis)
        {
            if (Widgets.Button($"{Icons.X} Audio", ButtonKind.Danger, audioWidth))
                AudioPadStopRequested?.Invoke();
            ImGui.SetItemTooltip("Stop Audio + Pad mode");
        }
        else
        {
            if (Widgets.Button($"{Icons.Headphones} Audio", ButtonKind.Secondary, audioWidth, enabled: entry.Address is not null))
                StartOrWakeAudioPad(entry, stored);
            ImGui.SetItemTooltip(entry.Address is null
                ? "Console offline"
                : "Audio + Controller only (no video window, 500 Hz input — ideal when playing on HDMI)");
        }

        ImGui.SameLine(0, gap);
        if (isSharingThis)
        {
            if (Widgets.Button($"{Icons.X} Web", ButtonKind.Danger, webWidth))
                webShare.RequestStop();
            ImGui.SetItemTooltip("Stop web link");
        }
        else
        {
            if (Widgets.Button($"{Icons.ExternalLink} Web", ButtonKind.Secondary, webWidth, enabled: entry.Address is not null))
                webShare.Start(entry);
            ImGui.SetItemTooltip(entry.Address is null ? "Console offline" : "Share PS5 via browser link");
        }
    }

    private void StartOrWakeAudioPad(ConsoleEntry entry, StoredConsole stored)
    {
        if (entry.State == ConsoleState.Ready && entry.Address is not null)
        {
            _wakeForAudioOnly = false;
            AudioPadRequested?.Invoke(entry, stored);
            return;
        }

        if (entry.Address is not null)
        {
            _wakeForAudioOnly = true;
            _wake.Begin(entry.HostId);
        }
    }

    private static bool IsAudioActiveFor(StreamController? audioStream, string hostId) =>
        audioStream is { AudioOnly: true, State: StreamState.Connecting or StreamState.Streaming, IsStopping: false }
        && string.Equals(audioStream.HostId, hostId, StringComparison.OrdinalIgnoreCase);

    private static void SameLineOrWrap(float nextWidth)
    {
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < nextWidth)
            ImGui.NewLine();
    }

    private void UpdateWake(IReadOnlyList<ConsoleEntry> entries)
    {
        if (_wake.HostId is not { } hostId)
            return;
        var entry = entries.FirstOrDefault(e => e.HostId == hostId);
        var stored = _services.Consoles.Find(hostId);
        if (stored is null)
        {
            _wakeForAudioOnly = false;
            _wake.Cancel();
            return;
        }

        switch (_wake.Update(entry))
        {
            case WakeStep.SendWakeup:
                var credential = WakeupCredential.FromRegistKey(stored.ToCredentials().RegistKey);
                _wakeJob.Run(() => _services.Browser.WakeAsync(entry!.Address!, credential));
                break;
            case WakeStep.Launch:
                var audioOnly = _wakeForAudioOnly;
                _wakeForAudioOnly = false;
                if (audioOnly)
                    AudioPadRequested?.Invoke(entry!, stored);
                else
                    PlayRequested?.Invoke(entry!, stored);
                break;
            case WakeStep.TimedOut:
                _wakeForAudioOnly = false;
                _notice = $"{stored.Nickname} did not turn on. Press the PS button on its controller, then try again.";
                break;
        }
    }

    private static Vector4 ToneColor(StatusTone tone) => tone switch
    {
        StatusTone.Ready => Theme.Success,
        StatusTone.Resting => Theme.Warning,
        StatusTone.Working => Theme.Accent,
        _ => Theme.TextFaint,
    };
}
