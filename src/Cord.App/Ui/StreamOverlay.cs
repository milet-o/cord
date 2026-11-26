using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using Cord.App.Platform;
using Cord.App.Streaming;
using Cord.Core.Consoles;
using Cord.Core.Settings;

namespace Cord.App.Ui;

/// <summary>Everything drawn over the video: mini-HUD, toast notifications, the F1 menu and the passcode prompt.</summary>
internal sealed class StreamOverlay
{
    private const double HintSeconds = 5;
    private const string PasscodeId = "passcode";
    private static readonly string[] FrameGenLabels = ["Off", "Low-Latency", "Smooth"];

    private readonly string _rendererName;
    private StreamController? _stream;
    private double _streamingSince = -1;
    private bool _menuVisible;
    private string _passcode = "";
    private long _copiedUntil;
    private string? _toastText;
    private long _toastUntil;

    public StreamOverlay(string rendererName = "SDL")
    {
        _rendererName = rendererName;
    }

    public bool MenuVisible => _menuVisible;

    public event Action<bool>? MouseLookToggled;

    public void ToggleMenu() => _menuVisible = !_menuVisible;

    public void ShowToast(string message, int durationMs = 4000)
    {
        _toastText = message;
        _toastUntil = Environment.TickCount64 + durationMs;
    }

    public void Draw(StreamController stream, ConsoleWebShare? webShare = null, SettingsStore? settingsStore = null, float refreshHz = 60f)
    {
        if (!ReferenceEquals(stream, _stream))
        {
            _stream = stream;
            _streamingSince = -1;
            _menuVisible = false;
        }

        if (stream.State == StreamState.Connecting)
        {
            DrawConnecting(stream);
        }
        else
        {
            if (_streamingSince < 0)
                _streamingSince = ImGui.GetTime();
            if (settingsStore?.Current.ShowMiniHud ?? true)
                DrawMiniHud(stream, settingsStore, refreshHz);
            DrawHint();
            if (_menuVisible)
                DrawMenu(stream, webShare, settingsStore, refreshHz);
            DrawToast();
        }

        DrawPasscode(stream);
    }

    private static void DrawConnecting(StreamController stream)
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Background);
        ImGui.Begin("connecting", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus);
        ImGui.PopStyleColor();

        var cardSize = Theme.Dp(460, 250);
        ImGui.SetCursorPos((viewport.WorkSize - cardSize) / 2);
        Widgets.BeginCard("card", cardSize.X, cardSize.Y);
        Widgets.Space(6);
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - Theme.Dp(46)) / 2);
        Widgets.Spinner(20);
        Widgets.Space(6);
        Widgets.Centered(stream.Message ?? "Connecting...", Theme.HeadingSize - 1, bold: true);
        Widgets.Centered("This usually takes a few seconds.", Theme.SmallSize, Theme.TextMuted);
        Widgets.Space(10);
        var width = Theme.Dp(160);
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - width) / 2);
        if (Widgets.Button("Cancel", ButtonKind.Secondary, width))
            _ = stream.StopAsync();
        Widgets.EndCard();
        ImGui.End();
    }

    private unsafe void DrawMiniHud(StreamController stream, SettingsStore? settingsStore, float refreshHz)
    {
        if (_menuVisible)
            return;
        var fgMode = settingsStore?.Current.FrameGen ?? FrameGenMode.Off;
        if (stream.Statistics(fgMode, refreshHz) is not { } stats)
            return;

        var viewport = ImGui.GetMainViewport();
        var drawList = ImGui.GetForegroundDrawList(viewport);
        var mouseTag = settingsStore?.Current.MouseRightStick == true ? "  ·  Mouse R-Stick" : "";
        var lossTag = stats.LossPercent > 0.05 ? $"  ·  {stats.LossPercent:0.1}% loss" : "";
        var fgTag = fgMode != FrameGenMode.Off ? $"  ·  FG: {stats.FrameGenStatus}" : "";
        var pipeTag = stats.Latency.AvgTotalMs > 0.05 ? $"  ·  +{stats.Latency.AvgTotalMs:0.0}ms client" : "";
        var text = $"{stats.RttMs.ToString(CultureInfo.InvariantCulture)} ms  ·  {stats.Fps:0} fps  ·  {stats.Height.ToString(CultureInfo.InvariantCulture)}p  ·  {stats.VideoMbps:0.0} Mbps{lossTag}  ·  ±{stats.JitterMs:0}ms{pipeTag}{fgTag}{mouseTag}";

        ImGui.PushFont(Theme.SemiBold, Theme.SmallSize - 1);
        var size = ImGui.CalcTextSize(text);
        ImGui.PopFont();
        var pad = Theme.Dp(10, 6);
        var min = viewport.Pos + Theme.Dp(12, 12);
        var max = min + size + pad * 2;
        drawList.AddRectFilled(min, max, OverlayBgU32(0.84f), Theme.Dp(6));
        drawList.AddRect(min, max, OverlayBorderU32(0.85f), Theme.Dp(6));
        drawList.AddText(Theme.SemiBold.Handle, Theme.Dp(Theme.SmallSize - 1), min + pad, OverlayTextU32(1f), text);
    }

    private unsafe void DrawHint()
    {
        var elapsed = ImGui.GetTime() - _streamingSince;
        if (elapsed > HintSeconds || _menuVisible)
            return;
        var alpha = (float)Math.Clamp(HintSeconds - elapsed, 0, 1);
        var viewport = ImGui.GetMainViewport();
        var drawList = ImGui.GetForegroundDrawList(viewport);
        const string text = "F1: menu  ·  F7: frame gen  ·  F8: mouse camera  ·  F9: save 30s clip  ·  F11: fullscreen";
        ImGui.PushFont(Theme.SemiBold, Theme.BodySize);
        var size = ImGui.CalcTextSize(text);
        ImGui.PopFont();
        var padding = Theme.Dp(20, 12);
        var min = new Vector2(viewport.Pos.X + (viewport.Size.X - size.X) / 2 - padding.X, viewport.Pos.Y + Theme.Dp(24));
        var max = min + size + padding * 2;
        drawList.AddRectFilled(min, max, OverlayBgU32(0.88f * alpha), Theme.Dp(8));
        drawList.AddRect(min, max, OverlayBorderU32(alpha), Theme.Dp(8));
        drawList.AddText(Theme.SemiBold.Handle, Theme.Dp(Theme.BodySize), min + padding, OverlayTextU32(alpha), text);
    }

    private unsafe void DrawToast()
    {
        if (_toastText is null || Environment.TickCount64 >= _toastUntil)
            return;

        var viewport = ImGui.GetMainViewport();
        var drawList = ImGui.GetForegroundDrawList(viewport);
        ImGui.PushFont(Theme.SemiBold, Theme.BodySize);
        var size = ImGui.CalcTextSize(_toastText);
        ImGui.PopFont();
        var pad = Theme.Dp(18, 10);
        var min = new Vector2(viewport.Pos.X + (viewport.Size.X - size.X) / 2 - pad.X, viewport.Pos.Y + viewport.Size.Y - Theme.Dp(64));
        var max = min + size + pad * 2;
        drawList.AddRectFilled(min, max, OverlayBgU32(0.92f), Theme.Dp(8));
        drawList.AddRect(min, max, Theme.U32(Theme.Accent), Theme.Dp(8));
        drawList.AddText(Theme.SemiBold.Handle, Theme.Dp(Theme.BodySize), min + pad, OverlayTextU32(1f), _toastText);
    }

    private static uint OverlayBgU32(float alpha) => Theme.U32(new Vector4(15f / 255f, 17f / 255f, 20f / 255f, alpha));

    private static uint OverlayBorderU32(float alpha) => Theme.U32(new Vector4(42f / 255f, 47f / 255f, 58f / 255f, alpha));

    private static uint OverlayTextU32(float alpha) => Theme.U32(new Vector4(242f / 255f, 243f / 255f, 245f / 255f, alpha));

    private void DrawMenu(StreamController stream, ConsoleWebShare? webShare, SettingsStore? settingsStore, float refreshHz)
    {
        ImGui.SetNextWindowPos(Theme.Dp(20, 20));
        ImGui.SetNextWindowSize(new Vector2(Theme.Dp(420), 0));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.WithAlpha(Theme.SurfaceRaised, 0.95f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Dp(22, 20));
        ImGui.Begin("stream-menu", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();

        Widgets.Heading("Stream");
        var fgSetting = settingsStore?.Current.FrameGen ?? FrameGenMode.Off;
        if (stream.Statistics(fgSetting, refreshHz) is { } stats)
        {
            Stat("Picture", $"{stats.Width} x {stats.Height}  ·  {stats.Fps:0} fps (src {stats.DetectedSourceFps})");
            Stat("Bandwidth", $"{stats.VideoMbps:0.0} Mbps");
            Stat("Network RTT / Jitter", $"{stats.RttMs.ToString(CultureInfo.InvariantCulture)} ms  ·  ±{stats.JitterMs:0.0} ms");
            Stat("Client pipeline", $"{stats.Latency.AvgTotalMs:0.0} ms (p95 {stats.Latency.P95TotalMs:0.0} ms)");
            Stat("Queue / Dec / Pres", $"{stats.Latency.AvgQueueMs:0.0} / {stats.Latency.AvgDecodeMs:0.0} / {stats.Latency.AvgPresentMs:0.0} ms");
            Stat("Input poll rate", $"{stats.Latency.InputPollHz:0} Hz");
            Stat("Packet loss", $"{stats.LossPercent:0.2}% ({stats.FramesLost.ToString(CultureInfo.InvariantCulture)} frames)");
            Stat("Decoder", stats.Decoder ?? "-");
            Stat("Renderer", _rendererName);
            Stat("Frame Gen", stats.FrameGenStatus);
            Stat("FEC recoveries", stats.FecRecoveries.ToString(CultureInfo.InvariantCulture));
        }

        if (settingsStore is not null)
        {
            Widgets.Space(4);
            var settings = settingsStore.Current;
            var fgIndex = (int)settings.FrameGen;
            Widgets.Text("Frame Generation (F7)", Theme.SmallSize, Theme.TextMuted);
            if (Widgets.Segmented("ov-fg", FrameGenLabels, ref fgIndex))
                settingsStore.Save(settings with { FrameGen = (FrameGenMode)fgIndex });

            var vsync = settings.VSync;
            if (ImGui.Checkbox("VSync (recommended for Frame Gen)", ref vsync))
                settingsStore.Save(settings with { VSync = vsync });

            if ((FrameGenMode)fgIndex != FrameGenMode.Off && !vsync)
                Widgets.Text("Frame Gen works best with VSync ON (disable only for lowest raw latency).", Theme.SmallSize, Theme.Warning);

            var sharpPercent = (int)MathF.Round(Math.Clamp(settings.Sharpness, 0f, 1f) * 100);
            Widgets.Text($"Sharpening (CAS): {sharpPercent.ToString(CultureInfo.InvariantCulture)}%", Theme.SmallSize, Theme.TextMuted);
            ImGui.SetNextItemWidth(Widgets.Fill);
            if (ImGui.SliderInt("##stream-sharp", ref sharpPercent, 0, 100, "%d%%"))
                settingsStore.Save(settings with { Sharpness = sharpPercent / 100f });

            var volPercent = (int)MathF.Round(settings.AudioVolume * 100);
            Widgets.Text($"Volume: {volPercent.ToString(CultureInfo.InvariantCulture)}%", Theme.SmallSize, Theme.TextMuted);
            ImGui.SetNextItemWidth(Widgets.Fill);
            if (ImGui.SliderInt("##stream-vol", ref volPercent, 0, 100, "%d%%"))
            {
                var vol = volPercent / 100f;
                stream.SetAudioVolume(vol);
                settingsStore.Save(settings with { AudioVolume = vol });
            }

            var miniHud = settings.ShowMiniHud;
            if (ImGui.Checkbox("Show corner Mini-HUD", ref miniHud))
                settingsStore.Save(settings with { ShowMiniHud = miniHud });

            var mouseStick = settings.MouseRightStick;
            if (ImGui.Checkbox("Mouse Right-Stick (F8)", ref mouseStick))
            {
                settingsStore.Save(settings with { MouseRightStick = mouseStick });
                MouseLookToggled?.Invoke(mouseStick);
            }
        }

        Widgets.Space(4);
        if (Widgets.Button("Save 30s Clip (F9)", ButtonKind.Primary, Widgets.Fill, stream.CanSaveReplayClip))
        {
            try
            {
                var path = stream.SaveReplayClip();
                ShowToast($"Clip saved: {Path.GetFileName(path)}");
            }
            catch (Exception e) when (e is InvalidOperationException or IOException)
            {
                ShowToast($"Clip error: {e.Message}");
            }
        }

        if (webShare?.HostId is not null)
        {
            Widgets.Space(6);
            Stat("Web link", $"{webShare.ClientCount.ToString(CultureInfo.InvariantCulture)} online · {webShare.PendingCount.ToString(CultureInfo.InvariantCulture)} pending");

            foreach (var p in webShare.PendingClients)
            {
                ImGui.PushID("ov-pend-" + p.Id);
                Widgets.Text($"Pending: {p.Name} ({p.Ip})", Theme.SmallSize, Theme.Warning);
                ImGui.SameLine();
                if (Widgets.Button("Accept"))
                    webShare.ApproveClient(p.Id, out _);
                ImGui.SameLine();
                if (Widgets.Button("Deny", ButtonKind.Danger))
                    webShare.DenyClient(p.Id);
                ImGui.PopID();
            }

            foreach (var m in webShare.Members)
            {
                ImGui.PushID("ov-mem-" + m.Id);
                Widgets.Text($"{m.Name}{(m.HasControl ? " [Ctrl]" : "")}", Theme.SmallSize);
                if (!m.HasControl)
                {
                    ImGui.SameLine();
                    if (Widgets.Button("Pass"))
                        webShare.AssignControl(m.Id);
                }

                ImGui.SameLine();
                if (Widgets.Button("Kick", ButtonKind.Danger))
                    webShare.KickClient(m.Id);
                ImGui.PopID();
            }

            var url = webShare.ShareUrl ?? webShare.LocalUrl;
            if (url is not null)
            {
                var copied = Environment.TickCount64 < _copiedUntil;
                if (Widgets.Button(copied ? $"{Icons.Check}  Copied!" : $"{Icons.Clipboard}  Copy link"))
                {
                    SdlHost.SetClipboardText(url);
                    _copiedUntil = Environment.TickCount64 + 2000;
                }

                ImGui.SameLine();
            }

            if (Widgets.Button(webShare.ControllerAllowed ? "Ctrl: ON" : "Ctrl: OFF"))
                webShare.ControllerAllowed = !webShare.ControllerAllowed;
            ImGui.SameLine();
            if (Widgets.Button(webShare.PsButtonLocked ? "PS: LOCK" : "PS: FREE"))
                webShare.PsButtonLocked = !webShare.PsButtonLocked;
        }

        Widgets.Space(6);
        if (Widgets.Button($"{Icons.Power}  Put PS5 in Rest Mode", ButtonKind.Secondary, Widgets.Fill))
        {
            _ = stream.PutConsoleToRestAsync();
            _menuVisible = false;
        }

        Widgets.Space(2);
        if (Widgets.Button($"{Icons.X}  Disconnect", ButtonKind.Danger, Widgets.Fill))
        {
            _ = stream.StopAsync();
            _menuVisible = false;
        }

        Widgets.Text("F1: hide menu  ·  F7: frame gen  ·  F8: mouse cam  ·  F9: 30s clip  ·  F11: fullscreen", Theme.SmallSize, Theme.TextFaint);
        ImGui.End();
    }

    private void DrawPasscode(StreamController stream)
    {
        if (!stream.PasscodeRequested)
            return;
        if (!ImGui.IsPopupOpen(PasscodeId))
            ImGui.OpenPopup(PasscodeId);
        if (!Widgets.BeginDialog(PasscodeId, 420))
            return;

        Widgets.Heading(stream.PasscodeRetry ? "Wrong passcode, try again" : "Console passcode");
        Widgets.Muted("Your PS5 asks for its login passcode.");
        ImGui.PushFont(Theme.SemiBold, 26);
        ImGui.SetNextItemWidth(Widgets.Fill);
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        var submitted = ImGui.InputText("##passcode", ref _passcode, 16, ImGuiInputTextFlags.Password | ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.PopFont();
        Widgets.Space(4);
        if (Widgets.Button("OK", ButtonKind.Primary, Theme.Dp(120)) || submitted)
        {
            stream.AnswerPasscode(_passcode);
            _passcode = "";
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (Widgets.Button("Cancel", ButtonKind.Ghost))
        {
            stream.AnswerPasscode(null);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private static void Stat(string label, string value)
    {
        Widgets.Text(label, Theme.SmallSize, Theme.TextMuted);
        ImGui.SameLine(Theme.Dp(170));
        Widgets.Text(value, Theme.SmallSize, bold: true);
    }
}
