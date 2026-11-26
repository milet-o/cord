using System.Runtime.InteropServices;
using Cord.App.Video;
using Cord.Core.Settings;
using Cord.Media;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Cord.App.Tests;

public sealed unsafe class D3D11ShaderTests
{
    [Fact]
    public void OpticalFlowTracksTranslatedNv12BlockAndSynthesizesIntermediatePosition()
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (!TryCreateTestDevice(out var device, out var context))
            return;

        using (device)
        using (context)
        using (var fg = new D3D11FrameGenerator(device, context))
        {
            const int width = 128;
            const int height = 128;

            // Frame 0: bright 24x24 square centered around x = 40..64, y = 52..76
            var frame0 = CreateNv12PlanesWithBlock(width, height, blockX: 40, blockY: 52, blockSize: 24);
            Assert.True(fg.IngestCpuPlanes(width, height, VideoPixelFormat.Nv12, frame0));

            // Identical frame should yield near-zero MAD
            Assert.True(fg.IngestCpuPlanes(width, height, VideoPixelFormat.Nv12, frame0));
            var duplicateMad = fg.ReadLatestMadBlocking();
            Assert.True(duplicateMad < FramePacer.DuplicateMadThreshold, $"Expected duplicate MAD < {FramePacer.DuplicateMadThreshold}, got {duplicateMad}");

            // Frame 1: translate bright square +8 px in X (x = 48..72)
            var frame1 = CreateNv12PlanesWithBlock(width, height, blockX: 48, blockY: 52, blockSize: 24);
            Assert.True(fg.IngestCpuPlanes(width, height, VideoPixelFormat.Nv12, frame1));
            var motionMad = fg.ReadLatestMadBlocking();
            Assert.True(motionMad > FramePacer.DuplicateMadThreshold, $"Expected motion MAD > {FramePacer.DuplicateMadThreshold}, got {motionMad}");

            fg.ComputeOpticalFlow();

            // Render interpolated frame at alpha = 0.5 into an offscreen RGBA target -> block centroid should move to ~56 (between 52 and 60)
            var rtTex = device.CreateTexture2D(new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
            });
            var rtv = device.CreateRenderTargetView(rtTex);
            var staging = device.CreateTexture2D(new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });

            using (rtTex)
            using (rtv)
            using (staging)
            {
                context.OMSetRenderTargets(rtv, null);
                context.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 1));
                fg.Draw(width, height, new FramePacingDecision(false, true, 0.5f, false), FrameGenMode.SmoothInterpolation, sharpness: 0.3f);

                context.CopyResource(staging, rtTex);
                var centroidInterp = MeasureBrightCentroidX(context, staging, width, height);
                // Frame0 centroid = 51.5, Frame1 centroid = 59.5 -> Interpolated at 0.5 should sit near 55.5
                Assert.InRange(centroidInterp, 53.0, 58.0);

                // Also verify Extrapolation path compiles & executes cleanly (centroid moves past 59.5)
                context.OMSetRenderTargets(rtv, null);
                context.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 1));
                fg.Draw(width, height, new FramePacingDecision(false, true, 0.5f, false), FrameGenMode.LowLatencyExtrapolation, sharpness: 0.3f);

                context.CopyResource(staging, rtTex);
                var centroidExtrap = MeasureBrightCentroidX(context, staging, width, height);
                Assert.InRange(centroidExtrap, 60.5, 66.5);
            }
        }
    }

    private static bool TryCreateTestDevice(out ID3D11Device device, out ID3D11DeviceContext context)
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out device!, out _, out context!).Success)
            return true;

        return D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device!, out _, out context!).Success;
    }

    private static VideoPlane[] CreateNv12PlanesWithBlock(int width, int height, int blockX, int blockY, int blockSize)
    {
        var yData = new byte[width * height];
        Array.Fill(yData, (byte)20);
        for (var y = blockY; y < blockY + blockSize; y++)
        {
            for (var x = blockX; x < blockX + blockSize; x++)
                yData[y * width + x] = 230;
        }

        var uvData = new byte[width * (height / 2)];
        Array.Fill(uvData, (byte)128);

        return [new VideoPlane(yData, width), new VideoPlane(uvData, width)];
    }

    private static double MeasureBrightCentroidX(ID3D11DeviceContext context, ID3D11Texture2D staging, int width, int height)
    {
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            double weightedX = 0;
            double totalWeight = 0;
            var basePtr = (byte*)mapped.DataPointer;
            var pitch = (int)mapped.RowPitch;
            for (var y = 0; y < height; y++)
            {
                var row = basePtr + y * pitch;
                for (var x = 0; x < width; x++)
                {
                    var r = row[x * 4 + 2];
                    if (r > 120)
                    {
                        weightedX += x * r;
                        totalWeight += r;
                    }
                }
            }

            return totalWeight > 0 ? weightedX / totalWeight : -1;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }
}
