using FFmpeg.AutoGen;

namespace Cord.Media;

/// <summary>
/// A decoded picture that stays in GPU memory: a slice of a D3D11 texture array owned by the
/// decoder. Dispose it as soon as it has been copied, so the decoder can reuse the surface.
/// </summary>
public sealed unsafe class GpuVideoFrame : IDisposable
{
    private AVFrame* _frame;

    internal GpuVideoFrame(AVFrame* frame, VideoPixelFormat format)
    {
        _frame = frame;
        Format = format;
        Width = frame->width;
        Height = frame->height;
        Texture = (nint)frame->data[0];
        ArrayIndex = (int)(nint)frame->data[1];
    }

    /// <summary>Native ID3D11Texture2D pointer (a texture array).</summary>
    public nint Texture { get; }

    /// <summary>Subresource (array slice) holding this picture.</summary>
    public int ArrayIndex { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary><see cref="VideoPixelFormat.Nv12"/> or <see cref="VideoPixelFormat.P010"/>.</summary>
    public VideoPixelFormat Format { get; }

    public void Dispose()
    {
        if (_frame == null)
            return;
        var frame = _frame;
        ffmpeg.av_frame_free(&frame);
        _frame = null;
    }
}

/// <summary>Receives the pictures produced by <see cref="VideoDecoder"/>.</summary>
public interface IVideoFrameSink
{
    /// <summary>A picture in system memory; the buffers are reused after the call returns.</summary>
    void OnCpuFrame(DecodedVideoFrame frame);

    /// <summary>A picture in GPU memory; the sink takes ownership and must dispose it.</summary>
    void OnGpuFrame(GpuVideoFrame frame);
}
