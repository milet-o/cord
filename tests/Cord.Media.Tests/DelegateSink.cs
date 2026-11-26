namespace Cord.Media.Tests;

internal sealed class DelegateSink(Action<DecodedVideoFrame> onCpuFrame, Action<GpuVideoFrame>? onGpuFrame = null) : IVideoFrameSink
{
    public void OnCpuFrame(DecodedVideoFrame frame) => onCpuFrame(frame);

    public void OnGpuFrame(GpuVideoFrame frame)
    {
        if (onGpuFrame is null)
            frame.Dispose();
        else
            onGpuFrame(frame);
    }
}
