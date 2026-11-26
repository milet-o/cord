using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Xunit.Abstractions;

namespace Cord.Media.Tests;

public class ZeroCopyTests(ITestOutputHelper output)
{
    [FfmpegFact]
    public void SharedD3D11Device_KeepsFramesOnGpuWithSamePixelsAsSoftware()
    {
        if (!OperatingSystem.IsWindows() || !TryCreateDevice(out var device, out var context))
        {
            output.WriteLine("No hardware D3D11 device; zero-copy not exercised.");
            return;
        }

        using (device)
        using (context)
        {
            using (var multithread = context.QueryInterface<ID3D11Multithread>())
                multithread.SetMultithreadProtected(true);

            var units = FfmpegTestEnvironment.SplitAccessUnits(
                File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "testsrc-320x240.h264")), VideoCodecKind.H264);

            byte[]? softwareLuma = null;
            using (var software = new VideoDecoder(VideoCodecKind.H264, new VideoDecoderOptions(HardwareAcceleration.None)))
            {
                foreach (var unit in units)
                {
                    software.Decode(unit, new DelegateSink(f => softwareLuma ??= CopyLuma(f.Planes[0].Data.Span, f.Planes[0].Stride, f.Width, f.Height)));
                    if (softwareLuma is not null)
                        break;
                }
            }

            byte[]? gpuLuma = null;
            var gpuFrames = 0;
            using (var decoder = new VideoDecoder(VideoCodecKind.H264, new VideoDecoderOptions(SharedD3D11Device: device.NativePointer)))
            {
                Assert.True(decoder.KeepsFramesOnGpu);
                foreach (var unit in units)
                {
                    decoder.Decode(unit, new DelegateSink(
                        _ => Assert.Fail("Expected GPU frames."),
                        frame =>
                        {
                            using (frame)
                            {
                                gpuFrames++;
                                Assert.Equal(VideoPixelFormat.Nv12, frame.Format);
                                gpuLuma ??= ReadLuma(device, context, frame);
                            }
                        }));
                }
            }

            output.WriteLine($"GPU frames: {gpuFrames}");
            Assert.InRange(gpuFrames, 9, 10);
            Assert.NotNull(softwareLuma);
            Assert.Equal(softwareLuma, gpuLuma);
        }
    }

    private static bool TryCreateDevice(out ID3D11Device device, out ID3D11DeviceContext context)
    {
        var result = D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.VideoSupport, [FeatureLevel.Level_11_0], out device!, out context!);
        return result.Success;
    }

    private static unsafe byte[] ReadLuma(ID3D11Device device, ID3D11DeviceContext context, GpuVideoFrame frame)
    {
        var description = new Texture2DDescription(Format.NV12, (uint)frame.Width, (uint)frame.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
        using var staging = device.CreateTexture2D(description);

        Marshal.AddRef(frame.Texture);
        using var source = new ID3D11Texture2D(frame.Texture);
        context.CopySubresourceRegion(staging, 0, 0, 0, 0, source, (uint)frame.ArrayIndex, new Box(0, 0, 0, frame.Width, frame.Height, 1));

        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            return CopyLuma(new ReadOnlySpan<byte>((void*)mapped.DataPointer, (int)mapped.RowPitch * frame.Height), (int)mapped.RowPitch, frame.Width, frame.Height);
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private static byte[] CopyLuma(ReadOnlySpan<byte> plane, int stride, int width, int height)
    {
        var luma = new byte[width * height];
        for (var y = 0; y < height; y++)
            plane.Slice(y * stride, width).CopyTo(luma.AsSpan(y * width));
        return luma;
    }
}
