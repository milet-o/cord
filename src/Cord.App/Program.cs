using System.Diagnostics;
using Hexa.NET.ImGui;
using Hexa.NET.SDL3;
using Cord.App.Audio;
using Cord.App.Input;
using Cord.App.Platform;
using Cord.App.Streaming;
using Cord.App.Ui;
using Cord.App.Video;
using Cord.Core.Consoles;
using Cord.Core.Settings;
using Cord.Core.Storage;
using Cord.Protocol.Input;

namespace Cord.App;

internal static class Program
{
    private const int ScreenshotFrame = 90;

    private static int Main()
    {
        using var host = new SdlHost($"Cord {AppVersion.Display}", 1280, 800);
        Theme.LoadFonts(ImGui.GetIO());
        Logo.Load(host);
        var services = new AppServices(AppPaths.Default());
        var forcedTheme = Environment.GetEnvironmentVariable("CORD_THEME");
        var initialLight = string.Equals(forcedTheme, "light", StringComparison.OrdinalIgnoreCase)
            || (!string.Equals(forcedTheme, "dark", StringComparison.OrdinalIgnoreCase) && services.Settings.Current.LightTheme);
        Theme.Apply(host.ContentScale, initialLight);
        var screenshotPath = Environment.GetEnvironmentVariable("CORD_SCREENSHOT");
        if (screenshotPath is null)
        {
            services.Browser.Start();
            services.Updates.StartBackgroundCheck();
        }
        using var presenter = new VideoPresenter(host);
        using var gamepad = new GamepadInput();
        using var haptics = new HapticsOutput();
        using var tray = new SystemTray();
        using var shell = new Shell(services);
        var overlay = new StreamOverlay(host.RendererName);
        StreamController? stream = null;
        StreamController? audioStream = null;
        HighResolutionTimerScope? timerScope = null;
        var fullscreen = false;
        var running = true;
        var lastHiddenFollowTs = 0L;

        tray.ShowRequested += () =>
        {
            tray.HideIcon();
            host.ShowWindow();
        };
        tray.StopWebShareRequested += () => services.WebShare.RequestStop();
        tray.ExitRequested += () => running = false;
        shell.ExitForUpdateRequested += () => running = false;
        shell.HideToTrayRequested += () =>
        {
            tray.ShowIcon();
            host.HideWindow();
        };

        overlay.MouseLookToggled += enabled =>
        {
            KeyboardInput.ResetMouse();
            host.SetRelativeMouseMode(enabled && !overlay.MenuVisible);
        };

        shell.PlayRequested += (entry, console) =>
        {
            if (entry.Address is null)
                return;
            tray.HideIcon();
            host.ShowWindow();
            audioStream?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            audioStream = null;
            stream?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            gamepad.ResetFeedback();
            haptics.SetEnabled(false);
            presenter.Clear();
            KeyboardInput.ResetMouse();
            stream = new StreamController(presenter, haptics, services.WebShare, entry.HostId);
            stream.Start(services.Tables, entry.Address, console, services.Settings.Current, gamepad.IsDualSense || !gamepad.Connected);
            fullscreen = services.Settings.Current.Fullscreen;
            host.SetFullscreen(fullscreen);
            host.SetRelativeMouseMode(services.Settings.Current.MouseRightStick);
        };

        shell.AudioPadRequested += (entry, console) =>
        {
            if (entry.Address is null)
                return;
            if (audioStream is { AudioOnly: true, State: StreamState.Connecting or StreamState.Streaming, IsStopping: false }
                && string.Equals(audioStream.HostId, entry.HostId, StringComparison.OrdinalIgnoreCase))
            {
                _ = audioStream.StopAsync();
                return;
            }

            stream?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            stream = null;
            audioStream?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            gamepad.ResetFeedback();
            haptics.SetEnabled(false);
            audioStream = new StreamController(presenter, haptics, services.WebShare, entry.HostId);
            audioStream.StartAudioOnly(services.Tables, entry.Address, console, services.Settings.Current, gamepad.IsDualSense || !gamepad.Connected);
        };

        shell.AudioPadStopRequested += () => _ = audioStream?.StopAsync();

        void SampleAndSendInput(StreamController activeStream)
        {
            if (ImGui.GetIO().WantCaptureKeyboard)
                return;
            var currentSettings = services.Settings.Current;
            var kb = KeyboardInput.Read(currentSettings.KeyBindings, currentSettings.MouseRightStick && !overlay.MenuVisible);
            activeStream.UpdateInput(MergeControllerInput(gamepad.Read(kb), services.WebShare));
        }

        gamepad.Refresh();
        var frameCount = 0;
        while (running)
        {
            frameCount++;
            if (tray.IsVisible)
                tray.PumpMessages();
            var streaming = stream is { State: StreamState.Connecting or StreamState.Streaming, IsStopping: false };
            var audioActive = audioStream is { AudioOnly: true, State: StreamState.Connecting or StreamState.Streaming, IsStopping: false };
            SdlHost.PumpEvents(e =>
            {
                var type = (SDLEventType)e.Type;
                if (type == SDLEventType.Quit)
                    running = false;
                if (type == SDLEventType.WindowCloseRequested)
                {
                    if (streaming)
                    {
                        _ = stream!.StopAsync();
                    }
                    else if (SystemTray.IsSupported && services.Settings.Current.MinimizeToTray && (services.WebShare.HostId is not null || audioActive))
                    {
                        tray.ShowIcon();
                        host.HideWindow();
                    }
                    else
                    {
                        running = false;
                    }
                }

                if (type is SDLEventType.WindowPixelSizeChanged or SDLEventType.WindowResized)
                    host.OnWindowResized();
                if (type is SDLEventType.GamepadAdded or SDLEventType.GamepadRemoved)
                    gamepad.Refresh();
                if (type is SDLEventType.AudioDeviceAdded or SDLEventType.AudioDeviceRemoved)
                    haptics.Refresh();
                if (type == SDLEventType.WindowDisplayScaleChanged)
                    Theme.Apply(host.ContentScale, services.Settings.Current.LightTheme);

                if (streaming && !overlay.MenuVisible && services.Settings.Current.MouseRightStick)
                {
                    if (type == SDLEventType.MouseMotion)
                        KeyboardInput.AddMouseMotion(e.Motion.Xrel, e.Motion.Yrel, services.Settings.Current.MouseSensitivity);
                    else if (type == SDLEventType.MouseButtonDown)
                        KeyboardInput.SetMouseButton(e.Button.Button, true);
                    else if (type == SDLEventType.MouseButtonUp)
                        KeyboardInput.SetMouseButton(e.Button.Button, false);
                }

                if (type == SDLEventType.KeyDown && e.Key.Repeat == 0)
                {
                    if (e.Key.Scancode == SDLScancode.F1 && streaming)
                    {
                        overlay.ToggleMenu();
                        host.SetRelativeMouseMode(services.Settings.Current.MouseRightStick && !overlay.MenuVisible);
                    }
                    else if (e.Key.Scancode == SDLScancode.F7 && streaming)
                    {
                        var nextMode = services.Settings.Current.FrameGen switch
                        {
                            FrameGenMode.Off => FrameGenMode.LowLatencyExtrapolation,
                            FrameGenMode.LowLatencyExtrapolation => FrameGenMode.SmoothInterpolation,
                            _ => FrameGenMode.Off,
                        };
                        services.Settings.Save(services.Settings.Current with { FrameGen = nextMode });
                        var label = nextMode switch
                        {
                            FrameGenMode.LowLatencyExtrapolation => "Frame Gen: Low-Latency Extrapolation",
                            FrameGenMode.SmoothInterpolation => "Frame Gen: Smooth Interpolation",
                            _ => "Frame Gen: Off",
                        };
                        overlay.ShowToast(label, 2200);
                    }
                    else if (e.Key.Scancode == SDLScancode.F8 && streaming)
                    {
                        var next = !services.Settings.Current.MouseRightStick;
                        services.Settings.Save(services.Settings.Current with { MouseRightStick = next });
                        KeyboardInput.ResetMouse();
                        host.SetRelativeMouseMode(next && !overlay.MenuVisible);
                        overlay.ShowToast(next ? "Mouse Right-Stick: ON (F8)" : "Mouse Right-Stick: OFF (F8)", 2000);
                    }
                    else if (e.Key.Scancode == SDLScancode.F9 && streaming && stream is not null)
                    {
                        try
                        {
                            var clipPath = stream.SaveReplayClip();
                            overlay.ShowToast($"Clip saved: {Path.GetFileName(clipPath)}");
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or IOException)
                        {
                            overlay.ShowToast($"Clip error: {ex.Message}");
                        }
                    }
                    else if (e.Key.Scancode == SDLScancode.F11)
                    {
                        host.SetFullscreen(fullscreen = !fullscreen);
                    }
                }
            });

            streaming = stream is { State: StreamState.Connecting or StreamState.Streaming, IsStopping: false };
            audioActive = audioStream is { AudioOnly: true, State: StreamState.Connecting or StreamState.Streaming, IsStopping: false };
            var wantTimerBoost = (streaming && services.Settings.Current.LowLatencyMode) || audioActive;
            if (wantTimerBoost && timerScope is null)
                timerScope = new HighResolutionTimerScope();
            else if (!wantTimerBoost && timerScope is not null)
            {
                timerScope.Dispose();
                timerScope = null;
            }

            if (host.IsWindowHidden && !streaming)
            {
                if (Stopwatch.GetTimestamp() - lastHiddenFollowTs >= Stopwatch.Frequency / 10)
                {
                    lastHiddenFollowTs = Stopwatch.GetTimestamp();
                    var snapshot = services.Browser.Snapshot();
                    services.Sharing.Follow(snapshot);
                    services.WebShare.Follow(snapshot);
                }

                if (audioActive)
                {
                    SdlHost.PumpEvents(e =>
                    {
                        var type = (SDLEventType)e.Type;
                        if (type == SDLEventType.Quit)
                            running = false;
                        else if (type is SDLEventType.GamepadAdded or SDLEventType.GamepadRemoved)
                            gamepad.Refresh();
                        else if (type is SDLEventType.AudioDeviceAdded or SDLEventType.AudioDeviceRemoved)
                            haptics.Refresh();
                    });
                    haptics.SetEnabled(gamepad.IsUsbDualSense);
                    while (audioStream!.GamepadFeedback.TryDequeue(out var feedback))
                        feedback(gamepad);
                    audioStream.UpdateInput(gamepad.Read(ControllerState.Idle));
                    Thread.Sleep(2);
                }
                else
                {
                    haptics.SetEnabled(false);
                    Thread.Sleep(100);
                }

                continue;
            }

            host.SetVSync(services.Settings.Current.VSync);
            host.BeginFrame();

            haptics.SetEnabled((streaming || audioActive) && gamepad.IsUsbDualSense);
            if (!streaming)
                host.SetRelativeMouseMode(false);
            if (!streaming && !audioActive)
            {
                stream?.GamepadFeedback.Clear();
                audioStream?.GamepadFeedback.Clear();
                gamepad.ResetFeedback();
            }

            if (streaming)
            {
                var (width, height) = host.OutputSize;
                var currentSettings = services.Settings.Current;
                stream!.DecodePending();
                presenter.Draw(width, height, currentSettings, stream.Latency);
                while (stream!.GamepadFeedback.TryDequeue(out var feedback))
                    feedback(gamepad);
                SampleAndSendInput(stream);
                overlay.Draw(stream, services.WebShare, services.Settings, host.RefreshRate);
            }
            else
            {
                if (audioActive)
                {
                    while (audioStream!.GamepadFeedback.TryDequeue(out var feedback))
                        feedback(gamepad);
                    audioStream.UpdateInput(gamepad.Read(ControllerState.Idle));
                }

                if (fullscreen && stream is not null)
                    host.SetFullscreen(fullscreen = false);
                shell.Draw(stream, audioStream, gamepad.Connected, gamepad.Name);
            }

            var capture = screenshotPath is not null && frameCount == ScreenshotFrame;
            var pollVideo = streaming && services.Settings.Current.LowLatencyMode && host.D3D11 is not null;
            var pollAudio = !streaming && audioActive && host.D3D11 is not null;
            var pollDuringWait = pollVideo || pollAudio;
            host.EndFrame(
                capture ? screenshotPath : null,
                pollDuringWait,
                pollVideo
                    ? () =>
                    {
                        SDL.PumpEvents();
                        SampleAndSendInput(stream!);
                    }
                    : pollAudio
                        ? () =>
                        {
                            SDL.PumpEvents();
                            audioStream!.UpdateInput(gamepad.Read(ControllerState.Idle));
                        }
                        : null,
                pollAudio ? 2u : 4u);

            if (capture)
                running = false;
        }

        timerScope?.Dispose();
        host.SetRelativeMouseMode(false);
        haptics.SetEnabled(false);
        gamepad.ResetFeedback();
        stream?.GamepadFeedback.Clear();
        audioStream?.GamepadFeedback.Clear();
        audioStream?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        stream?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Logo.Unload();
        return 0;
    }

    private static ControllerState MergeControllerInput(ControllerState local, ConsoleWebShare webShare)
    {
        if (!webShare.TryGetRemoteInput(out var remote))
            return local;

        return local with
        {
            Buttons = local.Buttons | remote.Buttons,
            L2 = Math.Max(local.L2, remote.L2),
            R2 = Math.Max(local.R2, remote.R2),
            LeftX = local.LeftX != 0 ? local.LeftX : remote.LeftX,
            LeftY = local.LeftY != 0 ? local.LeftY : remote.LeftY,
            RightX = local.RightX != 0 ? local.RightX : remote.RightX,
            RightY = local.RightY != 0 ? local.RightY : remote.RightY,
        };
    }
}
