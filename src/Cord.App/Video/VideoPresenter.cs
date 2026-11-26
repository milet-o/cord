using System.Diagnostics;
using System.Runtime.InteropServices;
using Hexa.NET.SDL3;
using Cord.App.Platform;
using Cord.App.Streaming;
using Cord.Core.Settings;
using Cord.Media;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace Cord.App.Video;

/// <summary>
/// Hands the newest decoded picture from the decoder thread to the render thread and draws it
/// letterboxed. On Windows with <see cref="D3D11Host"/>, executes the native D3D11 shader
/// pipeline (direct NV12/P010 conversion, optical flow Frame Gen, AMD CAS sharpening); elsewhere
/// uses SDL_Renderer textures.
/// </summary>
internal sealed unsafe class VideoPresenter : IVideoFrameSink, IDisposable
{
    private const string TexturePointerProperty = "SDL.texture.d3d11.texture";

    private readonly SdlHost _host;
    private readonly SDLRendererPtr _renderer;
    private readonly ID3D11Device? _device;
    private readonly ID3D11DeviceContext? _context;
    private readonly D3D11FrameGenerator? _frameGenerator;
    private readonly FramePacer _pacer = new();
    private readonly Lock _lock = new();

    private PendingFrame? _pending;
    private PendingFrame? _spare;
    private GpuVideoFrame? _pendingGpu;
    private long _pendingEnqueuedTs;
    private long _pendingDecodeStartTs;
    private long _pendingDecodedTs;
    private bool _pendingFrameReported;

    private SDLTexturePtr _texture;
    private ID3D11Resource? _textureResource;
    private (int Width, int Height, VideoPixelFormat Format) _textureShape;

    public VideoPresenter(SdlHost host)
    {
        _host = host;
        _renderer = host.Renderer;
        if (host.D3D11 is { } d3d11)
        {
            _device = d3d11.Device;
            _context = d3d11.Context;
            _frameGenerator = new D3D11FrameGenerator(d3d11.Device, d3d11.Context);
            SupportsMultithreadedDecode = true;
            return;
        }

        if (host.D3D11Device != 0 && OperatingSystem.IsWindows())
        {
            // SDL creates this device single-threaded: everything that touches it must run on the render thread.
            Marshal.AddRef(host.D3D11Device);
            _device = new ID3D11Device(host.D3D11Device);
            _context = _device.ImmediateContext;
        }
    }

    /// <summary>Device to share with the decoder so frames stay on the GPU, or zero.</summary>
    public nint SharedDevice => _device?.NativePointer ?? 0;

    public bool SupportsMultithreadedDecode { get; }

    public long FramesPresented { get; private set; }

    public long GeneratedFramesPresented => _pacer.GeneratedFrames;

    public int DetectedSourceFps => _pacer.DetectedSourceFps;

    public string FrameGenStatus(FrameGenMode mode, float refreshHz) =>
        _frameGenerator is null ? "Off (SDL)" : _pacer.StatusLabel(mode, refreshHz);

    public (int Width, int Height) VideoSize => _frameGenerator is not null
        ? (_frameGenerator.VideoWidth, _frameGenerator.VideoHeight)
        : (_textureShape.Width, _textureShape.Height);

    /// <summary>Called by the decode loop right before decoding each queued packet so timestamps flow into <see cref="LatencyTracker"/>.</summary>
    public void MarkFrameDecodeTiming(long enqueuedTimestamp, long decodeStartTimestamp)
    {
        lock (_lock)
        {
            _pendingEnqueuedTs = enqueuedTimestamp;
            _pendingDecodeStartTs = decodeStartTimestamp;
        }
    }

    public void OnGpuFrame(GpuVideoFrame frame)
    {
        GpuVideoFrame? replaced;
        var decodedTs = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            replaced = _pendingGpu;
            _pendingGpu = frame;
            _pendingDecodedTs = decodedTs;
            _pendingFrameReported = false;
        }

        replaced?.Dispose();
    }

    public void OnCpuFrame(DecodedVideoFrame frame)
    {
        PendingFrame target;
        lock (_lock)
        {
            target = _spare ?? new PendingFrame();
            _spare = null;
        }

        target.CopyFrom(frame);
        var decodedTs = Stopwatch.GetTimestamp();

        lock (_lock)
        {
            _spare = _pending;
            _pending = target;
            _pendingDecodedTs = decodedTs;
            _pendingFrameReported = false;
        }
    }

    public void Draw(int outputWidth, int outputHeight, AppSettings settings, LatencyTracker? latencyTracker = null)
    {
        PendingFrame? frame;
        GpuVideoFrame? gpuFrame;
        long enqueuedTs;
        long decodeStartTs;
        long decodedTs;
        bool shouldReportLatency;

        lock (_lock)
        {
            frame = _pending;
            _pending = null;
            gpuFrame = _pendingGpu;
            _pendingGpu = null;
            enqueuedTs = _pendingEnqueuedTs;
            decodeStartTs = _pendingDecodeStartTs;
            decodedTs = _pendingDecodedTs;
            shouldReportLatency = !_pendingFrameReported && (frame is not null || gpuFrame is not null);
            if (shouldReportLatency)
                _pendingFrameReported = true;
        }

        if (_frameGenerator is not null)
        {
            DrawDirectD3D11(outputWidth, outputHeight, settings, latencyTracker, frame, gpuFrame, enqueuedTs, decodeStartTs, decodedTs, shouldReportLatency);
            return;
        }

        DrawSdlFallback(outputWidth, outputHeight, latencyTracker, frame, gpuFrame, enqueuedTs, decodeStartTs, decodedTs, shouldReportLatency);
    }

    public void Clear()
    {
        GpuVideoFrame? gpuFrame;
        lock (_lock)
        {
            _pending = null;
            gpuFrame = _pendingGpu;
            _pendingGpu = null;
            _pendingFrameReported = false;
        }

        gpuFrame?.Dispose();
        _frameGenerator?.Clear();
        _pacer.Reset();
        DestroyTexture();
    }

    public void Dispose()
    {
        Clear();
        _frameGenerator?.Dispose();
        if (!SupportsMultithreadedDecode)
        {
            _context?.Dispose();
            _device?.Dispose();
        }
    }

    private void DrawDirectD3D11(
        int outputWidth,
        int outputHeight,
        AppSettings settings,
        LatencyTracker? latencyTracker,
        PendingFrame? frame,
        GpuVideoFrame? gpuFrame,
        long enqueuedTs,
        long decodeStartTs,
        long decodedTs,
        bool shouldReportLatency)
    {
        var ingested = false;
        if (gpuFrame is not null)
        {
            using (gpuFrame)
                ingested = _frameGenerator!.IngestGpuFrame(gpuFrame);
        }
        else if (frame is not null)
        {
            ingested = _frameGenerator!.IngestCpuPlanes(frame.Width, frame.Height, frame.Format, frame.AsVideoPlanes());
            lock (_lock)
                _spare ??= frame;
        }

        if (!_frameGenerator!.HasFrame)
            return;

        var nowMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        var isFreshUnique = false;
        if (ingested)
        {
            FramesPresented++;
            var mad = _frameGenerator.TryReadLatestMad(out var readMad) ? readMad : 0.01f;
            var needFlow = _pacer.OnNewSourceFrame(mad, nowMs, settings.FrameGen, out var sceneCut);
            isFreshUnique = !sceneCut && !_pacer.IsStaticScene && mad > FramePacer.DuplicateMadThreshold;
            if (sceneCut || _pacer.IsStaticScene)
                _frameGenerator.ResetTemporalFlow();
            if (needFlow)
                _frameGenerator.ComputeOpticalFlow();
        }

        var refreshHz = _host.RefreshRate > 0 ? _host.RefreshRate : 60f;
        var pacing = _pacer.EvaluateRenderTick(settings.FrameGen, refreshHz, nowMs, isFreshUnique);
        _frameGenerator.Draw(outputWidth, outputHeight, pacing, settings.FrameGen, settings.Sharpness);

        if (shouldReportLatency && latencyTracker is not null)
            latencyTracker.RecordFrame(enqueuedTs, decodeStartTs, decodedTs, Stopwatch.GetTimestamp());
    }

    private void DrawSdlFallback(
        int outputWidth,
        int outputHeight,
        LatencyTracker? latencyTracker,
        PendingFrame? frame,
        GpuVideoFrame? gpuFrame,
        long enqueuedTs,
        long decodeStartTs,
        long decodedTs,
        bool shouldReportLatency)
    {
        if (gpuFrame is not null)
        {
            using (gpuFrame)
            {
                if (CopyGpuFrame(gpuFrame))
                    FramesPresented++;
            }
        }
        else if (frame is not null)
        {
            Upload(frame);
            lock (_lock)
                _spare ??= frame;
            FramesPresented++;
        }

        if (_texture.IsNull)
            return;

        var (width, height, _) = _textureShape;
        var scale = Math.Min((float)outputWidth / width, (float)outputHeight / height);
        var destination = new SDLFRect
        {
            W = width * scale,
            H = height * scale,
            X = (outputWidth - (width * scale)) / 2,
            Y = (outputHeight - (height * scale)) / 2,
        };
        SDL.RenderTexture(_renderer, _texture, (SDLFRect*)null, &destination);

        if (shouldReportLatency && latencyTracker is not null)
            latencyTracker.RecordFrame(enqueuedTs, decodeStartTs, decodedTs, Stopwatch.GetTimestamp());
    }

    private bool CopyGpuFrame(GpuVideoFrame frame)
    {
        if (_context is null || !EnsureTexture(frame.Width, frame.Height, frame.Format, SDLTextureAccess.Static))
            return false;

        if (_textureResource is null)
        {
            var pointer = (nint)SDL.GetPointerProperty(SDL.GetTextureProperties(_texture), TexturePointerProperty, null);
            if (pointer == 0)
                return false;
            Marshal.AddRef(pointer);
            _textureResource = new ID3D11Resource(pointer);
        }

        Marshal.AddRef(frame.Texture);
        using var source = new ID3D11Resource(frame.Texture);
        SDL.FlushRenderer(_renderer);
        _context.CopySubresourceRegion(_textureResource, 0, 0, 0, 0, source, (uint)frame.ArrayIndex, new Box(0, 0, 0, frame.Width, frame.Height, 1));
        return true;
    }

    private void Upload(PendingFrame frame)
    {
        if (!EnsureTexture(frame.Width, frame.Height, frame.Format, SDLTextureAccess.Streaming))
            return;

        fixed (byte* data = frame.Buffer)
        {
            var planes = frame.Planes;
            if (frame.Format == VideoPixelFormat.Yuv420P)
            {
                SDL.UpdateYUVTexture(_texture, (SDLRect*)null,
                    data + planes[0].Offset, planes[0].Stride,
                    data + planes[1].Offset, planes[1].Stride,
                    data + planes[2].Offset, planes[2].Stride);
            }
            else
            {
                SDL.UpdateNVTexture(_texture, (SDLRect*)null,
                    data + planes[0].Offset, planes[0].Stride,
                    data + planes[1].Offset, planes[1].Stride);
            }
        }
    }

    private bool EnsureTexture(int width, int height, VideoPixelFormat format, SDLTextureAccess access)
    {
        var shape = (width, height, format);
        if (!_texture.IsNull && shape == _textureShape)
            return true;

        DestroyTexture();
        var pixelFormat = format switch
        {
            VideoPixelFormat.Nv12 => SDLPixelFormat.Nv12,
            VideoPixelFormat.P010 => SDLPixelFormat.P010,
            _ => SDLPixelFormat.Iyuv,
        };
        _texture = SDL.CreateTexture(_renderer, pixelFormat, access, width, height);
        _textureShape = shape;
        return !_texture.IsNull;
    }

    private void DestroyTexture()
    {
        _textureResource?.Dispose();
        _textureResource = null;
        if (_texture.IsNull)
            return;
        SDL.DestroyTexture(_texture);
        _texture = default;
    }

    private sealed class PendingFrame
    {
        public byte[] Buffer { get; private set; } = [];
        public List<(int Offset, int Stride)> Planes { get; } = [];
        public int Width { get; private set; }
        public int Height { get; private set; }
        public VideoPixelFormat Format { get; private set; }

        public void CopyFrom(DecodedVideoFrame frame)
        {
            var total = frame.Planes.Sum(p => p.Data.Length);
            if (Buffer.Length < total)
                Buffer = new byte[total];

            Planes.Clear();
            var offset = 0;
            foreach (var plane in frame.Planes)
            {
                plane.Data.Span.CopyTo(Buffer.AsSpan(offset));
                Planes.Add((offset, plane.Stride));
                offset += plane.Data.Length;
            }

            Width = frame.Width;
            Height = frame.Height;
            Format = frame.Format;
        }

        public List<VideoPlane> AsVideoPlanes()
        {
            _videoPlanes.Clear();
            for (var i = 0; i < Planes.Count; i++)
            {
                var (offset, stride) = Planes[i];
                var nextOffset = i + 1 < Planes.Count ? Planes[i + 1].Offset : Buffer.Length;
                _videoPlanes.Add(new VideoPlane(Buffer.AsMemory(offset, nextOffset - offset), stride));
            }

            return _videoPlanes;
        }

        private readonly List<VideoPlane> _videoPlanes = [];
    }
}
