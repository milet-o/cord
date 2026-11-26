using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using ImGuiImplD3D11 = Hexa.NET.ImGui.Backends.D3D11.ImGuiImplD3D11;
using ImGuiD3D11DevicePtr = Hexa.NET.ImGui.Backends.D3D11.ID3D11DevicePtr;
using ImGuiD3D11ContextPtr = Hexa.NET.ImGui.Backends.D3D11.ID3D11DeviceContextPtr;
using ImGuiImplSDL3 = Hexa.NET.ImGui.Backends.SDL3.ImGuiImplSDL3;
using Hexa.NET.SDL3;
using ImGuiSdlEvent = Hexa.NET.ImGui.Backends.SDL3.SDLEvent;
using ImGuiSdlRenderer = Hexa.NET.ImGui.Backends.SDL3.SDLRenderer;
using ImGuiSdlWindow = Hexa.NET.ImGui.Backends.SDL3.SDLWindow;
using SdlEvent = Hexa.NET.SDL3.SDLEvent;
using Vortice.Mathematics;

namespace Cord.App.Platform;

/// <summary>
/// Owns the SDL window, native D3D11 swapchain (on Windows) or SDL_Renderer fallback,
/// and the Dear ImGui context.
/// </summary>
internal sealed unsafe class SdlHost : IDisposable
{
    private const int MinimumWidth = 760;
    private const int MinimumHeight = 520;

    private readonly ImGuiContextPtr _imgui;
    private readonly FrameLimiter _limiter = new();
    private bool _vsync;
    private bool _relativeMouseMode;

    public SdlHost(string title, int width, int height)
    {
        // Closing the window while streaming only ends the stream, so it must not quit SDL.
        SDL.SetHint("SDL_QUIT_ON_LAST_WINDOW_CLOSE", "0");
        SDL.SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
        if (!SDL.Init(SDL.SDL_INIT_VIDEO | SDL.SDL_INIT_AUDIO | SDL.SDL_INIT_GAMEPAD | SDL.SDL_INIT_EVENTS))
            throw new InvalidOperationException($"SDL initialisation failed: {LastError}");

        var primaryDisplay = SDL.GetPrimaryDisplay();
        var scale = SDL.GetDisplayContentScale(primaryDisplay);
        if (scale <= 0)
            scale = 1;

        var targetW = (int)(width * scale);
        var targetH = (int)(height * scale);
        var minW = (int)(MinimumWidth * scale);
        var minH = (int)(MinimumHeight * scale);
        SDLRect usable;
        if (SDL.GetDisplayUsableBounds(primaryDisplay, &usable) && usable.W > 0 && usable.H > 0)
        {
            var maxW = Math.Max(320, usable.W - 48);
            var maxH = Math.Max(240, usable.H - 64);
            targetW = Math.Min(targetW, maxW);
            targetH = Math.Min(targetH, maxH);
            minW = Math.Min(minW, targetW);
            minH = Math.Min(minH, targetH);
        }

        Window = SDL.CreateWindow(title, targetW, targetH, (ulong)(SDLWindowFlags.Resizable | SDLWindowFlags.HighPixelDensity));
        if (Window.IsNull)
            throw new InvalidOperationException($"Could not create the window: {LastError}");
        SDL.SetWindowMinimumSize(Window, minW, minH);
        var icon = WindowIcon.Load();
        if (!icon.IsNull)
        {
            SDL.SetWindowIcon(Window, icon);
            SDL.DestroySurface(icon);
        }

        var forceSdlRenderer = string.Equals(Environment.GetEnvironmentVariable("CORD_FORCE_SDL_RENDERER"), "1", StringComparison.Ordinal);
        if (!forceSdlRenderer)
            D3D11 = D3D11Host.TryCreate(Window);

        _imgui = ImGui.CreateContext();
        ImGui.SetCurrentContext(_imgui);
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.NavEnableGamepad;
        io.IniFilename = null;

        if (D3D11 is not null)
        {
            _vsync = true;
            ImGuiImplSDL3.SetCurrentContext(_imgui);
            ImGuiImplSDL3.InitForD3D((ImGuiSdlWindow*)Window.Handle);
            ImGuiImplD3D11.SetCurrentContext(_imgui);
            ImGuiImplD3D11.Init(
                new ImGuiD3D11DevicePtr((Hexa.NET.ImGui.Backends.D3D11.ID3D11Device*)D3D11.DevicePointer),
                new ImGuiD3D11ContextPtr((Hexa.NET.ImGui.Backends.D3D11.ID3D11DeviceContext*)D3D11.ContextPointer));
        }
        else
        {
            if (OperatingSystem.IsWindows())
                Renderer = SDL.CreateRenderer(Window, "direct3d11");
            if (Renderer.IsNull)
                Renderer = SDL.CreateRenderer(Window, (byte*)null);
            if (Renderer.IsNull)
                throw new InvalidOperationException($"Could not create the renderer: {LastError}");
            SetVSync(true);
            _sdlD3D11Device = (nint)SDL.GetPointerProperty(SDL.GetRendererProperties(Renderer), "SDL.renderer.d3d11.device", null);

            ImGuiImplSDL3.SetCurrentContext(_imgui);
            ImGuiImplSDL3.InitForSDLRenderer((ImGuiSdlWindow*)Window.Handle, (ImGuiSdlRenderer*)Renderer.Handle);
            ImGuiImplSDL3.SDLRenderer3Init((ImGuiSdlRenderer*)Renderer.Handle);
        }
    }

    public SDLWindowPtr Window { get; }

    public SDLRendererPtr Renderer { get; }

    public D3D11Host? D3D11 { get; }

    /// <summary>The renderer's ID3D11Device (either native D3D11Host or SDL_Renderer), or zero.</summary>
    public nint D3D11Device => D3D11?.DevicePointer ?? _sdlD3D11Device;

    private readonly nint _sdlD3D11Device;

    public bool IsWindowHidden { get; private set; }

    public float RefreshRate
    {
        get
        {
            var mode = SDL.GetCurrentDisplayMode(SDL.GetDisplayForWindow(Window));
            return mode.IsNull ? 0 : mode.RefreshRate;
        }
    }

    public float ContentScale
    {
        get
        {
            var density = SDL.GetWindowPixelDensity(Window);
            var scale = SDL.GetWindowDisplayScale(Window);
            return density > 0 && scale > 0 ? scale / density : 1;
        }
    }

    public static string LastError => Marshal.PtrToStringUTF8((nint)SDL.GetError()) ?? "unknown error";

    public string RendererName => D3D11 is not null
        ? (D3D11.AllowTearingSupported ? "Direct3D 11 Flip (VRR/Tearing)" : "Direct3D 11 Flip")
        : Marshal.PtrToStringUTF8((nint)SDL.GetRendererName(Renderer)) ?? "unknown";

    public (int Width, int Height) OutputSize
    {
        get
        {
            if (D3D11 is not null)
                return (D3D11.Width, D3D11.Height);

            int width, height;
            SDL.GetCurrentRenderOutputSize(Renderer, &width, &height);
            return (width, height);
        }
    }

    public void OnWindowResized()
    {
        if (D3D11 is null)
            return;

        int width, height;
        SDL.GetWindowSizeInPixels(Window, &width, &height);
        D3D11.Resize(width, height);
    }

    public void SetVSync(bool enabled)
    {
        if (_vsync == enabled)
            return;
        _vsync = enabled;
        if (!Renderer.IsNull)
            SDL.SetRenderVSync(Renderer, enabled ? 1 : 0);
    }

    public void SetFullscreen(bool fullscreen) => SDL.SetWindowFullscreen(Window, fullscreen);

    public void HideWindow()
    {
        IsWindowHidden = true;
        SetRelativeMouseMode(false);
        SDL.HideWindow(Window);
    }

    public void ShowWindow()
    {
        IsWindowHidden = false;
        SDL.ShowWindow(Window);
        SDL.RaiseWindow(Window);
    }

    public void SetRelativeMouseMode(bool enabled)
    {
        if (_relativeMouseMode == enabled)
            return;
        _relativeMouseMode = enabled;
        SDL.SetWindowRelativeMouseMode(Window, enabled);
    }

    /// <summary>Polls events, forwarding them to ImGui and then to the handler.</summary>
    public static void PumpEvents(Action<SdlEvent> handler)
    {
        SdlEvent e;
        while (SDL.PollEvent(&e))
        {
            ImGuiImplSDL3.ProcessEvent((ImGuiSdlEvent*)&e);
            handler(e);
        }
    }

    public void BeginFrame()
    {
        var bg = Ui.Theme.Background;
        if (D3D11 is not null)
        {
            D3D11.BeginFrame(new Color4(bg.X, bg.Y, bg.Z, 1f));
            ImGuiImplD3D11.NewFrame();
            ImGuiImplSDL3.NewFrame();
            ImGui.NewFrame();
        }
        else
        {
            ImGuiImplSDL3.SDLRenderer3NewFrame();
            ImGuiImplSDL3.NewFrame();
            ImGui.NewFrame();
            SDL.SetRenderDrawColor(Renderer, (byte)(bg.X * 255), (byte)(bg.Y * 255), (byte)(bg.Z * 255), 255);
            SDL.RenderClear(Renderer);
        }
    }

    public void EndFrame(string? screenshotPath = null, bool pollInputDuringWait = false, Action? onInputSlice = null, uint inputSliceMs = 4)
    {
        ImGui.Render();
        if (D3D11 is not null)
        {
            D3D11.BindBackBuffer();
            ImGuiImplD3D11.RenderDrawData(ImGui.GetDrawData());
            if (screenshotPath is not null)
                D3D11.SaveScreenshot(screenshotPath);
            D3D11.Present(_vsync);
            D3D11.WaitForNextFrame(pollInputDuringWait, onInputSlice, inputSliceMs);
            if (!_vsync)
                _limiter.Wait(FrameLimiter.MinimumFrameNanoseconds(RefreshRate, vsync: false));
        }
        else
        {
            ImGuiImplSDL3.SDLRenderer3RenderDrawData(ImGui.GetDrawData(), (ImGuiSdlRenderer*)Renderer.Handle);
            if (screenshotPath is not null)
                SaveScreenshot(screenshotPath);
            SDL.RenderPresent(Renderer);
            _limiter.Wait(FrameLimiter.MinimumFrameNanoseconds(RefreshRate, _vsync));
        }
    }

    public static void OpenUrl(string url) => SDL.OpenURL(url);

    public static void SetClipboardText(string text) => SDL.SetClipboardText(text);

    public static string? ClipboardText()
    {
        var text = SDL.GetClipboardText();
        if (text == null)
            return null;
        var value = Marshal.PtrToStringUTF8((nint)text);
        SDL.Free(text);
        return value;
    }

    /// <summary>Saves the current render target as a BMP file (used for automated smoke tests).</summary>
    public bool SaveScreenshot(string path)
    {
        if (D3D11 is not null)
            return D3D11.SaveScreenshot(path);

        var surface = SDL.RenderReadPixels(Renderer, (SDLRect*)null);
        if (surface.IsNull)
            return false;
        var saved = SDL.SaveBMP(surface, path);
        SDL.DestroySurface(surface);
        return saved;
    }

    public void Dispose()
    {
        if (D3D11 is not null)
        {
            ImGuiImplD3D11.Shutdown();
            ImGuiImplSDL3.Shutdown();
            ImGui.DestroyContext(_imgui);
            D3D11.Dispose();
        }
        else
        {
            ImGuiImplSDL3.SDLRenderer3Shutdown();
            ImGuiImplSDL3.Shutdown();
            ImGui.DestroyContext(_imgui);
            SDL.DestroyRenderer(Renderer);
        }

        SDL.DestroyWindow(Window);
        SDL.Quit();
    }
}
