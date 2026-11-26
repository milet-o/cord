using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Cord.Media;

public enum VideoCodecKind
{
    H264,
    Hevc,
}

public enum HardwareAcceleration
{
    /// <summary>Use the platform's hardware decoder when available, otherwise software.</summary>
    Auto,
    None,
}

/// <param name="SharedD3D11Device">
/// Native ID3D11Device of the renderer. When set (Windows only), pictures are decoded on that
/// device and delivered as <see cref="GpuVideoFrame"/>s without leaving GPU memory. FFmpeg then
/// runs single-threaded, so creating the decoder and calling <see cref="VideoDecoder.Decode"/>
/// on the renderer's thread is enough even for devices created without multithread support.
/// </param>
public sealed record VideoDecoderOptions(
    HardwareAcceleration Acceleration = HardwareAcceleration.Auto,
    nint SharedD3D11Device = 0);

/// <summary>
/// Decodes Annex B access units with FFmpeg, preferring hardware decoding (D3D11VA on Windows,
/// VAAPI on Linux). Pictures are either kept on the GPU (shared D3D11 device) or copied back to
/// system memory.
/// </summary>
public sealed unsafe class VideoDecoder : IDisposable
{
    // Surfaces the presenter may hold on top of what the decoder needs for references.
    private const int ExtraHardwareFrames = 4;

    private readonly AVCodecContext* _context;
    private readonly AVPacket* _packet;
    private readonly AVFrame* _frame;
    private readonly AVFrame* _transferFrame;
    private readonly DecodedVideoFrame _output = new();
    private byte[] _buffer = [];
    private byte[] _concatBuffer = [];
    private bool _disposed;

    public VideoDecoder(VideoCodecKind codec, VideoDecoderOptions? options = null)
    {
        options ??= new VideoDecoderOptions();
        FfmpegLibrary.Load();
        var decoder = ffmpeg.avcodec_find_decoder(codec == VideoCodecKind.Hevc ? AVCodecID.AV_CODEC_ID_HEVC : AVCodecID.AV_CODEC_ID_H264);
        if (decoder == null)
            throw new InvalidOperationException($"FFmpeg has no {codec} decoder.");

        _context = ffmpeg.avcodec_alloc_context3(decoder);
        _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _context->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
        _context->thread_count = 0;
        _context->thread_type = ffmpeg.FF_THREAD_SLICE;

        if (options.Acceleration == HardwareAcceleration.Auto)
        {
            if (options.SharedD3D11Device != 0 && OperatingSystem.IsWindows() && TryAttachSharedD3D11Device(options.SharedD3D11Device))
            {
                KeepsFramesOnGpu = true;
                HardwareDevice = "d3d11va";
            }
            else
            {
                HardwareDevice = TryAttachHardwareDevice();
            }
        }

        Check(ffmpeg.avcodec_open2(_context, decoder, null), "open decoder");
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        _transferFrame = ffmpeg.av_frame_alloc();
    }

    /// <summary>Name of the hardware device type in use, or null for software decoding.</summary>
    public string? HardwareDevice { get; }

    /// <summary>True when pictures are delivered as <see cref="GpuVideoFrame"/>s.</summary>
    public bool KeepsFramesOnGpu { get; }

    /// <summary>Decodes one access unit (optionally prepending <paramref name="header"/>) and hands every picture produced to <paramref name="sink"/>.</summary>
    public void Decode(ReadOnlySpan<byte> accessUnit, IVideoFrameSink sink, ReadOnlySpan<byte> header = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (header.IsEmpty)
        {
            DecodeAccessUnit(accessUnit, sink);
            return;
        }

        var total = header.Length + accessUnit.Length;
        if (_concatBuffer.Length < total)
            _concatBuffer = new byte[total];
        header.CopyTo(_concatBuffer);
        accessUnit.CopyTo(_concatBuffer.AsSpan(header.Length));
        DecodeAccessUnit(_concatBuffer.AsSpan(0, total), sink);
    }

    private void DecodeAccessUnit(ReadOnlySpan<byte> accessUnit, IVideoFrameSink sink)
    {
        fixed (byte* data = accessUnit)
        {
            _packet->data = data;
            _packet->size = accessUnit.Length;
            var sent = ffmpeg.avcodec_send_packet(_context, _packet);
            _packet->data = null;
            _packet->size = 0;
            if (sent < 0 && sent != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                return;
        }

        while (ffmpeg.avcodec_receive_frame(_context, _frame) == 0)
        {
            Emit(sink);
            ffmpeg.av_frame_unref(_frame);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var context = _context;
        ffmpeg.avcodec_free_context(&context);
        var packet = _packet;
        ffmpeg.av_packet_free(&packet);
        var frame = _frame;
        ffmpeg.av_frame_free(&frame);
        var transfer = _transferFrame;
        ffmpeg.av_frame_free(&transfer);
    }

    private string? TryAttachHardwareDevice()
    {
        var type = OperatingSystem.IsWindows() ? AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA : AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI;
        AVBufferRef* device = null;
        if (ffmpeg.av_hwdevice_ctx_create(&device, type, null, null, 0) < 0)
            return null;
        _context->hw_device_ctx = ffmpeg.av_buffer_ref(device);
        ffmpeg.av_buffer_unref(&device);
        return ffmpeg.av_hwdevice_get_type_name(type);
    }

    private bool TryAttachSharedD3D11Device(nint device)
    {
        var buffer = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (buffer == null)
            return false;

        // FFmpeg releases the device when the context is freed, so it gets its own reference.
        Marshal.AddRef(device);
        var deviceContext = (AVHWDeviceContext*)buffer->data;
        ((AVD3D11VADeviceContext*)deviceContext->hwctx)->device = (ID3D11Device*)device;
        if (ffmpeg.av_hwdevice_ctx_init(buffer) < 0)
        {
            ffmpeg.av_buffer_unref(&buffer);
            return false;
        }

        _context->hw_device_ctx = buffer;
        _context->extra_hw_frames = ExtraHardwareFrames;
        _context->thread_count = 1;
        return true;
    }

    private void Emit(IVideoFrameSink sink)
    {
        if (KeepsFramesOnGpu && (AVPixelFormat)_frame->format == AVPixelFormat.AV_PIX_FMT_D3D11 && _frame->hw_frames_ctx != null)
        {
            var softwareFormat = ((AVHWFramesContext*)_frame->hw_frames_ctx->data)->sw_format;
            VideoPixelFormat? format = softwareFormat switch
            {
                AVPixelFormat.AV_PIX_FMT_NV12 => VideoPixelFormat.Nv12,
                AVPixelFormat.AV_PIX_FMT_P010LE => VideoPixelFormat.P010,
                _ => null,
            };
            if (format is not null)
            {
                sink.OnGpuFrame(new GpuVideoFrame(ffmpeg.av_frame_clone(_frame), format.Value));
                return;
            }
        }

        EmitCpuFrame(sink);
    }

    private void EmitCpuFrame(IVideoFrameSink sink)
    {
        var source = _frame;
        var hardware = _frame->hw_frames_ctx != null;
        if (hardware)
        {
            ffmpeg.av_frame_unref(_transferFrame);
            if (ffmpeg.av_hwframe_transfer_data(_transferFrame, _frame, 0) < 0)
                return;
            source = _transferFrame;
        }

        var format = (AVPixelFormat)source->format;
        VideoPixelFormat pixelFormat;
        int[] planeHeights;
        var chromaHeight = (source->height + 1) / 2;
        switch (format)
        {
            case AVPixelFormat.AV_PIX_FMT_NV12:
                pixelFormat = VideoPixelFormat.Nv12;
                planeHeights = [source->height, chromaHeight];
                break;
            case AVPixelFormat.AV_PIX_FMT_P010LE:
                pixelFormat = VideoPixelFormat.P010;
                planeHeights = [source->height, chromaHeight];
                break;
            case AVPixelFormat.AV_PIX_FMT_YUV420P:
            case AVPixelFormat.AV_PIX_FMT_YUVJ420P:
                pixelFormat = VideoPixelFormat.Yuv420P;
                planeHeights = [source->height, chromaHeight, chromaHeight];
                break;
            default:
                return;
        }

        var total = 0;
        for (var i = 0; i < planeHeights.Length; i++)
            total += source->linesize[(uint)i] * planeHeights[i];
        if (_buffer.Length < total)
            _buffer = new byte[total];

        _output._planes.Clear();
        var offset = 0;
        for (var i = 0; i < planeHeights.Length; i++)
        {
            var stride = source->linesize[(uint)i];
            var size = stride * planeHeights[i];
            new ReadOnlySpan<byte>(source->data[(uint)i], size).CopyTo(_buffer.AsSpan(offset));
            _output._planes.Add(new VideoPlane(_buffer.AsMemory(offset, size), stride));
            offset += size;
        }

        _output.Width = source->width;
        _output.Height = source->height;
        _output.Format = pixelFormat;
        _output.HardwareDecoded = hardware;
        sink.OnCpuFrame(_output);
    }

    private static void Check(int result, string action)
    {
        if (result < 0)
            throw new InvalidOperationException($"FFmpeg failed to {action}: {FfmpegLibrary.ErrorText(result)}");
    }
}
