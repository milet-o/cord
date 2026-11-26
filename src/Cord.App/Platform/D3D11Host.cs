using System.Runtime.InteropServices;
using Hexa.NET.SDL3;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Cord.App.Platform;

/// <summary>
/// Direct Windows D3D11 swapchain host: FlipDiscard, MaximumFrameLatency=1, AllowTearing/VRR,
/// waitable-object 250 Hz input slicing, and multithread-safe ID3D11Device for D3D11VA decoding.
/// </summary>
internal sealed unsafe partial class D3D11Host : IDisposable
{
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x00000102;
    private const uint InputSliceMs = 4;
    private const int MaxWaitSlices = 13;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain1;
    private readonly IDXGISwapChain2? _swapChain2;
    private readonly nint _waitableObject;
    private readonly bool _allowTearingSupported;
    private ID3D11RenderTargetView? _backBufferRtv;
    private int _width;
    private int _height;
    private bool _disposed;

    private D3D11Host(
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGISwapChain1 swapChain1,
        IDXGISwapChain2? swapChain2,
        nint waitableObject,
        bool allowTearingSupported,
        int width,
        int height)
    {
        _device = device;
        _context = context;
        _swapChain1 = swapChain1;
        _swapChain2 = swapChain2;
        _waitableObject = waitableObject;
        _allowTearingSupported = allowTearingSupported;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        CreateRenderTarget();
    }

    public ID3D11Device Device => _device;

    public ID3D11DeviceContext Context => _context;

    public nint DevicePointer => _device.NativePointer;

    public nint ContextPointer => _context.NativePointer;

    public bool AllowTearingSupported => _allowTearingSupported;

    public int Width => _width;

    public int Height => _height;

    public static D3D11Host? TryCreate(SDLWindowPtr window)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var hwnd = (nint)SDL.GetPointerProperty(SDL.GetWindowProperties(window), "SDL.window.win32.hwnd", null);
        if (hwnd == 0)
            return null;

        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        IDXGIDevice? dxgiDevice = null;
        IDXGIAdapter? adapter = null;
        IDXGIFactory2? factory = null;
        IDXGISwapChain1? swapChain1 = null;
        IDXGISwapChain2? swapChain2 = null;

        try
        {
            var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
            FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
            if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out device, out _, out context).Failure || device is null || context is null)
                return null;

            using (var multithread = device.QueryInterfaceOrNull<ID3D11Multithread>())
                multithread?.SetMultithreadProtected(true);

            dxgiDevice = device.QueryInterface<IDXGIDevice>();
            adapter = dxgiDevice.GetAdapter();
            factory = adapter.GetParent<IDXGIFactory2>();
            factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

            bool tearingSupported;
            using (var factory5 = factory.QueryInterfaceOrNull<IDXGIFactory5>())
                tearingSupported = factory5 is not null && factory5.PresentAllowTearing;

            int width;
            int height;
            SDL.GetWindowSizeInPixels(window, &width, &height);
            width = Math.Max(1, width);
            height = Math.Max(1, height);

            var swapFlags = SwapChainFlags.FrameLatencyWaitableObject;
            if (tearingSupported)
                swapFlags |= SwapChainFlags.AllowTearing;

            var desc = new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 3,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
                Flags = swapFlags,
            };

            swapChain1 = factory.CreateSwapChainForHwnd(device, hwnd, desc, null, null);
            swapChain2 = swapChain1.QueryInterfaceOrNull<IDXGISwapChain2>();
            nint waitable = 0;
            if (swapChain2 is not null)
            {
                swapChain2.MaximumFrameLatency = 1;
                waitable = swapChain2.FrameLatencyWaitableObject;
            }

            var host = new D3D11Host(device, context, swapChain1, swapChain2, waitable, tearingSupported, width, height);
            // Ownership transferred to D3D11Host.
            device = null;
            context = null;
            swapChain1 = null;
            swapChain2 = null;
            return host;
        }
        catch
        {
            swapChain2?.Dispose();
            swapChain1?.Dispose();
            context?.Dispose();
            device?.Dispose();
            return null;
        }
        finally
        {
            factory?.Dispose();
            adapter?.Dispose();
            dxgiDevice?.Dispose();
        }
    }

    public void Resize(int width, int height)
    {
        if (_disposed)
            return;

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _width && height == _height)
            return;

        _context.UnsetRenderTargets();
        _backBufferRtv?.Dispose();
        _backBufferRtv = null;

        var flags = SwapChainFlags.FrameLatencyWaitableObject;
        if (_allowTearingSupported)
            flags |= SwapChainFlags.AllowTearing;

        var hr = _swapChain1.ResizeBuffers(3, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, flags);
        if (hr.Success)
        {
            _width = width;
            _height = height;
        }

        CreateRenderTarget();
    }

    public void BeginFrame(Color4 clearColor)
    {
        if (_backBufferRtv is null)
            CreateRenderTarget();
        if (_backBufferRtv is null)
            return;

        _context.OMSetRenderTargets(_backBufferRtv, null);
        _context.RSSetViewport(0, 0, _width, _height);
        _context.ClearRenderTargetView(_backBufferRtv, clearColor);
    }

    public void BindBackBuffer()
    {
        if (_backBufferRtv is not null)
            _context.OMSetRenderTargets(_backBufferRtv, null);
    }

    public void Present(bool vsync)
    {
        if (_disposed)
            return;

        if (vsync)
            _swapChain1.Present(1, PresentFlags.None);
        else
            _swapChain1.Present(0, _allowTearingSupported ? PresentFlags.AllowTearing : PresentFlags.None);
    }

    /// <summary>
    /// Waits on DXGI's frame latency handle in 4 ms slices while pumping input callbacks so controller
    /// updates continue at 250 Hz even when VSync paces presentation at 60 Hz.
    /// </summary>
    public void WaitForNextFrame(bool pollInputDuringWait, Action? onInputSlice, uint sliceMs = InputSliceMs)
    {
        if (_waitableObject == 0 || !OperatingSystem.IsWindows())
            return;

        if (!pollInputDuringWait || onInputSlice is null)
        {
            _ = WaitForSingleObject(_waitableObject, 50);
            return;
        }

        var stepMs = Math.Max(1u, sliceMs);
        var maxSlices = (int)Math.Max(1, 52 / stepMs);
        for (var i = 0; i < maxSlices; i++)
        {
            var status = WaitForSingleObject(_waitableObject, stepMs);
            if (status == WaitObject0)
                return;
            if (status != WaitTimeout)
                return;
            onInputSlice();
        }
    }

    public (ID3D11Texture2D Texture, ID3D11ShaderResourceView Srv)? CreateTextureFromSurface(SDLSurfacePtr surface)
    {
        if (surface.IsNull)
            return null;

        var converted = SDL.ConvertSurface(surface, SDLPixelFormat.Abgr8888);
        if (converted.IsNull)
            return null;

        try
        {
            var desc = new Texture2DDescription
            {
                Width = (uint)converted.W,
                Height = (uint)converted.H,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
            };

            var data = new SubresourceData((nint)converted.Pixels, (uint)converted.Pitch);
            var tex = _device.CreateTexture2D(desc, [data]);
            var srv = _device.CreateShaderResourceView(tex);
            return (tex, srv);
        }
        finally
        {
            SDL.DestroySurface(converted);
        }
    }

    public bool SaveScreenshot(string path)
    {
        if (_disposed)
            return false;

        using var backBuffer = _swapChain1.GetBuffer<ID3D11Texture2D>(0);
        var desc = backBuffer.Description;
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.MiscFlags = ResourceOptionFlags.None;

        using var staging = _device.CreateTexture2D(desc);
        _context.CopyResource(staging, backBuffer);
        var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var surface = SDL.CreateSurfaceFrom((int)desc.Width, (int)desc.Height, SDLPixelFormat.Argb8888, mapped.DataPointer, (int)mapped.RowPitch);
            if (surface.IsNull)
                return false;
            try
            {
                return SDL.SaveBMP(surface, path);
            }
            finally
            {
                SDL.DestroySurface(surface);
            }
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _context.UnsetRenderTargets();
        _backBufferRtv?.Dispose();
        _backBufferRtv = null;
        if (_waitableObject != 0 && OperatingSystem.IsWindows())
            _ = CloseHandle(_waitableObject);
        _swapChain2?.Dispose();
        _swapChain1.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    private void CreateRenderTarget()
    {
        using var backBuffer = _swapChain1.GetBuffer<ID3D11Texture2D>(0);
        _backBufferRtv = _device.CreateRenderTargetView(backBuffer);
    }

    [LibraryImport("kernel32.dll", SetLastError = false)]
    private static partial uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);
}
