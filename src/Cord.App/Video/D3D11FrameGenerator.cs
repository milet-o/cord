using System.Runtime.InteropServices;
using Cord.Core.Settings;
using Cord.Media;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Cord.App.Video;

/// <summary>
/// Full D3D11 compute + pixel-shader video pipeline:
/// - Direct NV12 / P010 / YUV420P -> RGBA + half-res luma conversion on GPU
/// - 4-level luma pyramid + non-blocking MAD readback for 30 fps duplicate &amp; scene-cut detection
/// - Bidirectional coarse-to-fine ZSAD Optical Flow + median/bilateral smoothing + FB consistency
/// - Low-latency Extrapolation, Smooth Interpolation, and AMD FidelityFX CAS sharpening
/// </summary>
internal sealed unsafe class D3D11FrameGenerator : IDisposable
{
    private const int MadStagingRingSize = 3;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;

    private readonly ID3D11ComputeShader _csYuvToRgba;
    private readonly ID3D11ComputeShader _csDownsampleLuma;
    private readonly ID3D11ComputeShader _csComputeMad;
    private readonly ID3D11ComputeShader _csOpticalFlow;
    private readonly ID3D11ComputeShader _csFlowSmooth;
    private readonly ID3D11VertexShader _vsFullscreen;
    private readonly ID3D11PixelShader _psPresent;

    private readonly ID3D11SamplerState _linearClampSampler;
    private readonly ID3D11Buffer _csParamsBuffer;
    private readonly ID3D11Buffer _psParamsBuffer;
    private readonly ID3D11Buffer _madGpuBuffer;
    private readonly ID3D11UnorderedAccessView _madUav;
    private readonly ID3D11Buffer[] _madStaging = new ID3D11Buffer[MadStagingRingSize];
    private int _madWriteSlot;
    private int _madTotalDispatched;

    private int _width;
    private int _height;
    private VideoPixelFormat _inputFormat;
    private bool _hasCurrFrame;
    private bool _hasPrevFrame;
    private bool _hasPrevFlow;
    private bool _disposed;

    // Input staging textures on GPU (Default for GPU-to-GPU copy, Dynamic for CPU upload)
    private ID3D11Texture2D? _gpuInputSemiPlanarTex;
    private ID3D11ShaderResourceView? _gpuInputYView;
    private ID3D11ShaderResourceView? _gpuInputUvView;
    private ID3D11Texture2D? _cpuInputSemiPlanarTex;
    private ID3D11ShaderResourceView? _cpuInputYView;
    private ID3D11ShaderResourceView? _cpuInputUvView;
    private ID3D11Texture2D? _inputPlanarY;
    private ID3D11Texture2D? _inputPlanarU;
    private ID3D11Texture2D? _inputPlanarV;
    private ID3D11ShaderResourceView? _inputPlanarYSrv;
    private ID3D11ShaderResourceView? _inputPlanarUSrv;
    private ID3D11ShaderResourceView? _inputPlanarVSrv;

    // Frame history + luma pyramid + bidirectional flow fields
    private TextureSlot? _currRgba;
    private TextureSlot? _prevRgba;
    private readonly TextureSlot?[] _currLuma = new TextureSlot?[4];
    private readonly TextureSlot?[] _prevLuma = new TextureSlot?[4];
    private TextureSlot? _flowFwd3;
    private TextureSlot? _flowFwd2;
    private TextureSlot? _flowFwd1;
    private TextureSlot? _flowBwd3;
    private TextureSlot? _flowBwd2;
    private TextureSlot? _flowBwd1;
    private TextureSlot? _flowSmoothed;
    private TextureSlot? _prevFlowSmoothed;

    public D3D11FrameGenerator(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;

        _csYuvToRgba = device.CreateComputeShader(D3D11ShaderCompiler.Compile(HlslSource, "CS_YuvToRgbaAndLuma", "cs_5_0"));
        _csDownsampleLuma = device.CreateComputeShader(D3D11ShaderCompiler.Compile(HlslSource, "CS_DownsampleLuma", "cs_5_0"));
        _csComputeMad = device.CreateComputeShader(D3D11ShaderCompiler.Compile(HlslSource, "CS_ComputeMad", "cs_5_0"));
        _csOpticalFlow = device.CreateComputeShader(D3D11ShaderCompiler.Compile(HlslSource, "CS_OpticalFlowLevel", "cs_5_0"));
        _csFlowSmooth = device.CreateComputeShader(D3D11ShaderCompiler.Compile(HlslSource, "CS_FlowSmooth", "cs_5_0"));
        _vsFullscreen = device.CreateVertexShader(D3D11ShaderCompiler.Compile(HlslSource, "VS_FullscreenQuad", "vs_5_0"));
        _psPresent = device.CreatePixelShader(D3D11ShaderCompiler.Compile(HlslSource, "PS_PresentFrameGenCas", "ps_5_0"));

        _linearClampSampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxLOD = float.MaxValue,
        });

        _csParamsBuffer = device.CreateBuffer(32, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        _psParamsBuffer = device.CreateBuffer(32, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);

        _madGpuBuffer = device.CreateBuffer(8, BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 4);
        _madUav = device.CreateUnorderedAccessView(_madGpuBuffer, new UnorderedAccessViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = 2 },
        });

        for (var i = 0; i < MadStagingRingSize; i++)
            _madStaging[i] = device.CreateBuffer(8, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, ResourceOptionFlags.BufferStructured, 4);
    }

    public int VideoWidth => _width;

    public int VideoHeight => _height;

    public bool HasFrame => _hasCurrFrame;

    public bool IngestGpuFrame(GpuVideoFrame frame)
    {
        if (_disposed || frame.Texture == 0)
            return false;

        EnsureResources(frame.Width, frame.Height, frame.Format, gpuInput: true);
        if (_gpuInputSemiPlanarTex is null)
            return false;

        RotateHistory();
        Marshal.AddRef(frame.Texture);
        using var src = new ID3D11Resource(frame.Texture);
        _context.CopySubresourceRegion(_gpuInputSemiPlanarTex, 0, 0, 0, 0, src, (uint)frame.ArrayIndex, new Box(0, 0, 0, frame.Width, frame.Height, 1));

        RunIngestCompute(frame.Format == VideoPixelFormat.P010 ? 1 : 0, fromGpuInput: true);
        return true;
    }

    public bool IngestCpuFrame(DecodedVideoFrame frame) =>
        IngestCpuPlanes(frame.Width, frame.Height, frame.Format, frame.Planes);

    public bool IngestCpuPlanes(int width, int height, VideoPixelFormat format, IReadOnlyList<VideoPlane> planes)
    {
        if (_disposed || planes.Count < 2)
            return false;

        EnsureResources(width, height, format, gpuInput: false);
        RotateHistory();

        if (format == VideoPixelFormat.Yuv420P)
        {
            if (_inputPlanarY is null || _inputPlanarU is null || _inputPlanarV is null || planes.Count < 3)
                return false;
            UploadPlane(_inputPlanarY, planes[0]);
            UploadPlane(_inputPlanarU, planes[1]);
            UploadPlane(_inputPlanarV, planes[2]);
            RunIngestCompute(2, fromGpuInput: false);
            return true;
        }

        if (_cpuInputSemiPlanarTex is null)
            return false;

        var uvHeight = (height + 1) / 2;
        var mapped = _context.Map(_cpuInputSemiPlanarTex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var dst = (byte*)mapped.DataPointer;
            var yStride = planes[0].Stride;
            var dstPitch = (int)mapped.RowPitch;
            fixed (byte* ySrc = planes[0].Data.Span)
            {
                var copyBytes = Math.Min(yStride, dstPitch);
                for (var row = 0; row < height; row++)
                    Buffer.MemoryCopy(ySrc + row * yStride, dst + row * dstPitch, dstPitch, copyBytes);
            }

            var uvDst = dst + height * dstPitch;
            var uvStride = planes[1].Stride;
            fixed (byte* uvSrc = planes[1].Data.Span)
            {
                var copyBytes = Math.Min(uvStride, dstPitch);
                for (var row = 0; row < uvHeight; row++)
                    Buffer.MemoryCopy(uvSrc + row * uvStride, uvDst + row * dstPitch, dstPitch, copyBytes);
            }
        }
        finally
        {
            _context.Unmap(_cpuInputSemiPlanarTex, 0);
        }

        RunIngestCompute(format == VideoPixelFormat.P010 ? 1 : 0, fromGpuInput: false);
        return true;
    }

    public bool TryReadLatestMad(out float mad)
    {
        mad = 0.01f;
        if (_madTotalDispatched < 2)
            return false;

        var readSlot = (_madWriteSlot + 1) % MadStagingRingSize;
        var hr = _context.Map(_madStaging[readSlot], 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out var mapped);
        if (hr.Failure)
            return false;

        try
        {
            var data = (uint*)mapped.DataPointer;
            var sum = data[0];
            var count = Math.Max(1u, data[1]);
            mad = sum / (count * 255.0f);
            return true;
        }
        finally
        {
            _context.Unmap(_madStaging[readSlot], 0);
        }
    }

    /// <summary>Synchronous MAD readback used by GPU unit tests.</summary>
    internal float ReadLatestMadBlocking()
    {
        var slot = (_madWriteSlot + MadStagingRingSize - 1) % MadStagingRingSize;
        var mapped = _context.Map(_madStaging[slot], 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var data = (uint*)mapped.DataPointer;
            return data[0] / (Math.Max(1u, data[1]) * 255.0f);
        }
        finally
        {
            _context.Unmap(_madStaging[slot], 0);
        }
    }

    public void ComputeOpticalFlow()
    {
        if (_disposed || !_hasCurrFrame || !_hasPrevFrame)
            return;

        if (_currLuma[3] is null || _prevLuma[3] is null || _flowFwd1 is null || _flowBwd1 is null || _flowSmoothed is null)
            return;

        // Forward flow: Curr -> Prev
        DispatchFlowLevel(_currLuma[3]!, _prevLuma[3]!, null, _flowFwd3!, pixelStep: 16f, searchRadius: 2);
        DispatchFlowLevel(_currLuma[2]!, _prevLuma[2]!, _flowFwd3, _flowFwd2!, pixelStep: 8f, searchRadius: 1);
        DispatchFlowLevel(_currLuma[1]!, _prevLuma[1]!, _flowFwd2, _flowFwd1, pixelStep: 4f, searchRadius: 1);

        // Backward flow: Prev -> Curr (for Forward-Backward occlusion & confidence check)
        DispatchFlowLevel(_prevLuma[3]!, _currLuma[3]!, null, _flowBwd3!, pixelStep: 16f, searchRadius: 2);
        DispatchFlowLevel(_prevLuma[2]!, _currLuma[2]!, _flowBwd3, _flowBwd2!, pixelStep: 8f, searchRadius: 1);
        DispatchFlowLevel(_prevLuma[1]!, _currLuma[1]!, _flowBwd2, _flowBwd1, pixelStep: 4f, searchRadius: 1);

        // Ping-pong smoothed flow so _prevFlowSmoothed holds the previous frame's flow and _flowSmoothed is distinct for writing
        if (_hasPrevFlow && _prevFlowSmoothed is not null)
            (_flowSmoothed, _prevFlowSmoothed) = (_prevFlowSmoothed, _flowSmoothed);

        // 3x3 median + edge-aware bilateral smooth + FB consistency confidence + temporal EMA
        WriteCsParams(_flowSmoothed.Width, _flowSmoothed.Height, _hasPrevFlow ? 1 : 0, 0, 1f / _width, 1f / _height);
        _context.CSSetShader(_csFlowSmooth);
        _context.CSSetConstantBuffer(0, _csParamsBuffer);
        _context.CSSetSampler(0, _linearClampSampler);
        _context.CSSetShaderResources(0, [_flowFwd1.Srv, _flowBwd1.Srv, _currLuma[1]!.Srv, _prevFlowSmoothed?.Srv ?? _flowFwd1.Srv]);
        _context.CSSetUnorderedAccessViews(0, [_flowSmoothed.Uav]);
        _context.Dispatch((uint)((_flowSmoothed.Width + 7) / 8), (uint)((_flowSmoothed.Height + 7) / 8), 1);
        UnbindCompute();
        _hasPrevFlow = true;
    }

    public void ResetTemporalFlow() => _hasPrevFlow = false;

    public void Draw(int outputWidth, int outputHeight, FramePacingDecision pacing, FrameGenMode mode, float sharpness)
    {
        if (_disposed || !_hasCurrFrame || _currRgba is null)
            return;

        var scale = Math.Min((float)outputWidth / _width, (float)outputHeight / _height);
        var vpW = _width * scale;
        var vpH = _height * scale;
        var vpX = (outputWidth - vpW) / 2f;
        var vpY = (outputHeight - vpH) / 2f;

        var psMode = 0;
        if (pacing.UseFrameGen && _hasPrevFrame)
            psMode = mode == FrameGenMode.LowLatencyExtrapolation ? 1 : 2;

        var primaryTex = pacing.PresentPreviousFrame && _prevRgba is not null ? _prevRgba : _currRgba;
        var secondaryTex = _currRgba;
        if (psMode == 2 && _prevRgba is not null)
        {
            primaryTex = _prevRgba;
            secondaryTex = _currRgba;
        }

        WritePsParams(pacing.Alpha, Math.Clamp(sharpness, 0f, 1f), 1f / _width, 1f / _height, psMode);

        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vsFullscreen);
        _context.PSSetShader(_psPresent);
        _context.PSSetConstantBuffer(0, _psParamsBuffer);
        _context.PSSetSampler(0, _linearClampSampler);
        _context.PSSetShaderResources(0, [primaryTex.Srv, secondaryTex.Srv, (_flowSmoothed ?? primaryTex).Srv]);
        _context.RSSetViewport(vpX, vpY, vpW, vpH);
        _context.Draw(3, 0);

        _context.PSSetShaderResources(0, [null!, null!, null!]);
        _context.RSSetViewport(0, 0, outputWidth, outputHeight);
    }

    public void Clear()
    {
        _hasCurrFrame = false;
        _hasPrevFrame = false;
        _hasPrevFlow = false;
        _madTotalDispatched = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        ReleaseResolutionResources();
        _madUav.Dispose();
        _madGpuBuffer.Dispose();
        foreach (var buf in _madStaging)
            buf.Dispose();
        _csParamsBuffer.Dispose();
        _psParamsBuffer.Dispose();
        _linearClampSampler.Dispose();
        _csYuvToRgba.Dispose();
        _csDownsampleLuma.Dispose();
        _csComputeMad.Dispose();
        _csOpticalFlow.Dispose();
        _csFlowSmooth.Dispose();
        _vsFullscreen.Dispose();
        _psPresent.Dispose();
    }

    private void RotateHistory()
    {
        if (_hasCurrFrame)
        {
            (_prevRgba, _currRgba) = (_currRgba, _prevRgba);
            for (var i = 0; i < 4; i++)
                (_prevLuma[i], _currLuma[i]) = (_currLuma[i], _prevLuma[i]);
            _hasPrevFrame = true;
        }

        _hasCurrFrame = true;
    }

    private void RunIngestCompute(int formatKind, bool fromGpuInput)
    {
        if (_currRgba is null || _currLuma[0] is null)
            return;

        // 1. YUV -> RGBA + L0 (half-res luma)
        WriteCsParams(_width, _height, formatKind, 0, 0f, 0f);
        _context.CSSetShader(_csYuvToRgba);
        _context.CSSetConstantBuffer(0, _csParamsBuffer);
        if (formatKind == 2)
            _context.CSSetShaderResources(0, [_inputPlanarYSrv!, _inputPlanarUSrv!, _inputPlanarVSrv!]);
        else if (fromGpuInput)
            _context.CSSetShaderResources(0, [_gpuInputYView!, _gpuInputUvView!, null!]);
        else
            _context.CSSetShaderResources(0, [_cpuInputYView!, _cpuInputUvView!, null!]);

        _context.CSSetUnorderedAccessViews(0, [_currRgba.Uav, _currLuma[0]!.Uav]);
        _context.Dispatch((uint)((_width + 7) / 8), (uint)((_height + 7) / 8), 1);
        UnbindCompute();

        // 2. Downsample pyramid L0 -> L1 -> L2 -> L3
        for (var level = 1; level < 4; level++)
        {
            var src = _currLuma[level - 1]!;
            var dst = _currLuma[level]!;
            WriteCsParams(dst.Width, dst.Height, src.Width, src.Height, 0f, 0f);
            _context.CSSetShader(_csDownsampleLuma);
            _context.CSSetConstantBuffer(0, _csParamsBuffer);
            _context.CSSetShaderResources(0, [src.Srv]);
            _context.CSSetUnorderedAccessViews(0, [dst.Uav]);
            _context.Dispatch((uint)((dst.Width + 7) / 8), (uint)((dst.Height + 7) / 8), 1);
            UnbindCompute();
        }

        // 3. Global MAD on L3
        if (_hasPrevFrame && _currLuma[3] is { } c3 && _prevLuma[3] is { } p3)
        {
            _context.ClearUnorderedAccessView(_madUav, new Int4(0, c3.Width * c3.Height, 0, 0));
            WriteCsParams(c3.Width, c3.Height, 0, 0, 0f, 0f);
            _context.CSSetShader(_csComputeMad);
            _context.CSSetConstantBuffer(0, _csParamsBuffer);
            _context.CSSetShaderResources(0, [c3.Srv, p3.Srv]);
            _context.CSSetUnorderedAccessViews(0, [_madUav]);
            _context.Dispatch((uint)((c3.Width + 15) / 16), (uint)((c3.Height + 15) / 16), 1);
            UnbindCompute();

            var slot = _madWriteSlot;
            _context.CopyResource(_madStaging[slot], _madGpuBuffer);
            _madWriteSlot = (slot + 1) % MadStagingRingSize;
            _madTotalDispatched++;
        }
    }

    private void DispatchFlowLevel(TextureSlot curr, TextureSlot prev, TextureSlot? coarseFlow, TextureSlot outFlow, float pixelStep, int searchRadius)
    {
        WriteCsParams(curr.Width, curr.Height, searchRadius, coarseFlow is not null ? 1 : 0, pixelStep, 1f / _width);
        _context.CSSetShader(_csOpticalFlow);
        _context.CSSetConstantBuffer(0, _csParamsBuffer);
        _context.CSSetSampler(0, _linearClampSampler);
        _context.CSSetShaderResources(0, [curr.Srv, prev.Srv, coarseFlow?.Srv ?? curr.Srv]);
        _context.CSSetUnorderedAccessViews(0, [outFlow.Uav]);
        _context.Dispatch((uint)((outFlow.Width + 7) / 8), (uint)((outFlow.Height + 7) / 8), 1);
        UnbindCompute();
    }

    private void UnbindCompute()
    {
        _context.CSSetShaderResources(0, [null!, null!, null!, null!]);
        _context.CSSetUnorderedAccessViews(0, [null!, null!]);
    }

    private void UploadPlane(ID3D11Texture2D texture, VideoPlane plane)
    {
        var height = (int)texture.Description.Height;
        var mapped = _context.Map(texture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var dst = (byte*)mapped.DataPointer;
            var dstPitch = (int)mapped.RowPitch;
            var copyBytes = Math.Min(plane.Stride, dstPitch);
            fixed (byte* src = plane.Data.Span)
            {
                for (var row = 0; row < height; row++)
                    Buffer.MemoryCopy(src + row * plane.Stride, dst + row * dstPitch, dstPitch, copyBytes);
            }
        }
        finally
        {
            _context.Unmap(texture, 0);
        }
    }

    private void WriteCsParams(int p0, int p1, int p2, int p3, float f0, float f1)
    {
        var mapped = _context.Map(_csParamsBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var ptr = (int*)mapped.DataPointer;
            ptr[0] = p0;
            ptr[1] = p1;
            ptr[2] = p2;
            ptr[3] = p3;
            var fptr = (float*)(ptr + 4);
            fptr[0] = f0;
            fptr[1] = f1;
            fptr[2] = _height > 0 ? 1f / _height : 0f;
            fptr[3] = 0f;
        }
        finally
        {
            _context.Unmap(_csParamsBuffer, 0);
        }
    }

    private void WritePsParams(float alpha, float sharpness, float invWidth, float invHeight, int mode)
    {
        var mapped = _context.Map(_psParamsBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var fptr = (float*)mapped.DataPointer;
            fptr[0] = alpha;
            fptr[1] = sharpness;
            fptr[2] = invWidth;
            fptr[3] = invHeight;
            var iptr = (int*)(fptr + 4);
            iptr[0] = mode;
            iptr[1] = _width;
            iptr[2] = _height;
            iptr[3] = 0;
        }
        finally
        {
            _context.Unmap(_psParamsBuffer, 0);
        }
    }

    private void EnsureResources(int width, int height, VideoPixelFormat format, bool gpuInput)
    {
        var sizeOrFormatChanged = width != _width || height != _height || format != _inputFormat || _currRgba is null;
        if (sizeOrFormatChanged)
        {
            ReleaseResolutionResources();
            _width = width;
            _height = height;
            _inputFormat = format;
            _hasCurrFrame = false;
            _hasPrevFrame = false;
            _madTotalDispatched = 0;

            _currRgba = CreateComputeSlot(width, height, Format.R8G8B8A8_UNorm);
            _prevRgba = CreateComputeSlot(width, height, Format.R8G8B8A8_UNorm);

            var lw = width;
            var lh = height;
            for (var i = 0; i < 4; i++)
            {
                lw = Math.Max(1, (lw + 1) / 2);
                lh = Math.Max(1, (lh + 1) / 2);
                _currLuma[i] = CreateComputeSlot(lw, lh, Format.R8_UNorm);
                _prevLuma[i] = CreateComputeSlot(lw, lh, Format.R8_UNorm);
            }

            _flowFwd3 = CreateComputeSlot(_currLuma[3]!.Width, _currLuma[3]!.Height, Format.R16G16_Float);
            _flowFwd2 = CreateComputeSlot(_currLuma[2]!.Width, _currLuma[2]!.Height, Format.R16G16_Float);
            _flowFwd1 = CreateComputeSlot(_currLuma[1]!.Width, _currLuma[1]!.Height, Format.R16G16_Float);
            _flowBwd3 = CreateComputeSlot(_currLuma[3]!.Width, _currLuma[3]!.Height, Format.R16G16_Float);
            _flowBwd2 = CreateComputeSlot(_currLuma[2]!.Width, _currLuma[2]!.Height, Format.R16G16_Float);
            _flowBwd1 = CreateComputeSlot(_currLuma[1]!.Width, _currLuma[1]!.Height, Format.R16G16_Float);
            _flowSmoothed = CreateComputeSlot(_currLuma[1]!.Width, _currLuma[1]!.Height, Format.R16G16B16A16_Float);
            _prevFlowSmoothed = CreateComputeSlot(_currLuma[1]!.Width, _currLuma[1]!.Height, Format.R16G16B16A16_Float);
            _hasPrevFlow = false;
        }

        if (format == VideoPixelFormat.Yuv420P)
        {
            if (_inputPlanarY is null)
            {
                var cw = Math.Max(1, (width + 1) / 2);
                var ch = Math.Max(1, (height + 1) / 2);
                _inputPlanarY = CreateDynamicTexture(width, height, Format.R8_UNorm);
                _inputPlanarU = CreateDynamicTexture(cw, ch, Format.R8_UNorm);
                _inputPlanarV = CreateDynamicTexture(cw, ch, Format.R8_UNorm);
                _inputPlanarYSrv = _device.CreateShaderResourceView(_inputPlanarY);
                _inputPlanarUSrv = _device.CreateShaderResourceView(_inputPlanarU);
                _inputPlanarVSrv = _device.CreateShaderResourceView(_inputPlanarV);
            }

            return;
        }

        var is10 = format == VideoPixelFormat.P010;
        var texFormat = is10 ? Format.P010 : Format.NV12;
        var ySrvFormat = is10 ? Format.R16_UNorm : Format.R8_UNorm;
        var uvSrvFormat = is10 ? Format.R16G16_UNorm : Format.R8G8_UNorm;

        if (gpuInput && _gpuInputSemiPlanarTex is null)
        {
            _gpuInputSemiPlanarTex = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = texFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            });
            _gpuInputYView = _device.CreateShaderResourceView(_gpuInputSemiPlanarTex, new ShaderResourceViewDescription(_gpuInputSemiPlanarTex, ShaderResourceViewDimension.Texture2D, ySrvFormat));
            _gpuInputUvView = _device.CreateShaderResourceView(_gpuInputSemiPlanarTex, new ShaderResourceViewDescription(_gpuInputSemiPlanarTex, ShaderResourceViewDimension.Texture2D, uvSrvFormat));
        }
        else if (!gpuInput && _cpuInputSemiPlanarTex is null)
        {
            _cpuInputSemiPlanarTex = CreateDynamicTexture(width, height, texFormat);
            _cpuInputYView = _device.CreateShaderResourceView(_cpuInputSemiPlanarTex, new ShaderResourceViewDescription(_cpuInputSemiPlanarTex, ShaderResourceViewDimension.Texture2D, ySrvFormat));
            _cpuInputUvView = _device.CreateShaderResourceView(_cpuInputSemiPlanarTex, new ShaderResourceViewDescription(_cpuInputSemiPlanarTex, ShaderResourceViewDimension.Texture2D, uvSrvFormat));
        }
    }

    private ID3D11Texture2D CreateDynamicTexture(int width, int height, Format format) =>
        _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

    private TextureSlot CreateComputeSlot(int width, int height, Format format)
    {
        var tex = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
        });
        return new TextureSlot(tex, _device.CreateShaderResourceView(tex), _device.CreateUnorderedAccessView(tex), width, height);
    }

    private void ReleaseResolutionResources()
    {
        _gpuInputYView?.Dispose();
        _gpuInputYView = null;
        _gpuInputUvView?.Dispose();
        _gpuInputUvView = null;
        _gpuInputSemiPlanarTex?.Dispose();
        _gpuInputSemiPlanarTex = null;

        _cpuInputYView?.Dispose();
        _cpuInputYView = null;
        _cpuInputUvView?.Dispose();
        _cpuInputUvView = null;
        _cpuInputSemiPlanarTex?.Dispose();
        _cpuInputSemiPlanarTex = null;

        _inputPlanarYSrv?.Dispose();
        _inputPlanarYSrv = null;
        _inputPlanarUSrv?.Dispose();
        _inputPlanarUSrv = null;
        _inputPlanarVSrv?.Dispose();
        _inputPlanarVSrv = null;
        _inputPlanarY?.Dispose();
        _inputPlanarY = null;
        _inputPlanarU?.Dispose();
        _inputPlanarU = null;
        _inputPlanarV?.Dispose();
        _inputPlanarV = null;

        _currRgba?.Dispose();
        _currRgba = null;
        _prevRgba?.Dispose();
        _prevRgba = null;
        for (var i = 0; i < 4; i++)
        {
            _currLuma[i]?.Dispose();
            _currLuma[i] = null;
            _prevLuma[i]?.Dispose();
            _prevLuma[i] = null;
        }

        _flowFwd3?.Dispose();
        _flowFwd3 = null;
        _flowFwd2?.Dispose();
        _flowFwd2 = null;
        _flowFwd1?.Dispose();
        _flowFwd1 = null;
        _flowBwd3?.Dispose();
        _flowBwd3 = null;
        _flowBwd2?.Dispose();
        _flowBwd2 = null;
        _flowBwd1?.Dispose();
        _flowBwd1 = null;
        _flowSmoothed?.Dispose();
        _flowSmoothed = null;
        _prevFlowSmoothed?.Dispose();
        _prevFlowSmoothed = null;
        _hasPrevFlow = false;
    }

    private sealed class TextureSlot(ID3D11Texture2D texture, ID3D11ShaderResourceView srv, ID3D11UnorderedAccessView uav, int width, int height) : IDisposable
    {
        public ID3D11Texture2D Texture { get; } = texture;
        public ID3D11ShaderResourceView Srv { get; } = srv;
        public ID3D11UnorderedAccessView Uav { get; } = uav;
        public int Width { get; } = width;
        public int Height { get; } = height;

        public void Dispose()
        {
            Uav.Dispose();
            Srv.Dispose();
            Texture.Dispose();
        }
    }

    private const string HlslSource = """
        cbuffer CsParams : register(b0)
        {
            int4 g_i;
            float4 g_f;
        };

        cbuffer PsParams : register(b0)
        {
            float g_alpha;
            float g_sharpness;
            float g_invW;
            float g_invH;
            int g_mode;
            int g_videoW;
            int g_videoH;
            int g_pad;
        };

        SamplerState g_linearClamp : register(s0);

        Texture2D<float> g_texY : register(t0);
        Texture2D<float2> g_texUV : register(t1);
        Texture2D<float> g_texV : register(t2);

        RWTexture2D<unorm float4> g_outRgba : register(u0);
        RWTexture2D<unorm float> g_outLuma : register(u1);

        float3 Yuv709ToRgb(float y, float u, float v)
        {
            float yn = max(0.0f, (y - 16.0f / 255.0f) * (255.0f / 219.0f));
            float un = (u - 0.5f) * (255.0f / 224.0f);
            float vn = (v - 0.5f) * (255.0f / 224.0f);
            return saturate(float3(
                yn + 1.5748f * vn,
                yn - 0.1873f * un - 0.4681f * vn,
                yn + 1.8556f * un));
        }

        float3 PqToLinear(float3 pq)
        {
            float3 p = pow(max(pq, 0.0f), 1.0f / 78.84375f);
            float3 num = max(p - 0.8359375f, 0.0f);
            float3 den = max(18.8515625f - 18.6875f * p, 1e-4f);
            return pow(num / den, 1.0f / 0.1593017578125f) * 100.0f;
        }

        float3 P010ToSdrRgb(float y, float u, float v)
        {
            float yn = max(0.0f, (y - 64.0f / 1023.0f) * (1023.0f / 876.0f));
            float un = (u - 0.5f) * (1023.0f / 896.0f);
            float vn = (v - 0.5f) * (1023.0f / 896.0f);
            float3 pqRgb = saturate(float3(
                yn + 1.4746f * vn,
                yn - 0.16455f * un - 0.57135f * vn,
                yn + 1.8814f * un));
            float3 hdr = PqToLinear(pqRgb);
            float3 mapped = hdr / (1.0f + hdr);
            return pow(saturate(mapped), 1.0f / 2.2f);
        }

        [numthreads(8, 8, 1)]
        void CS_YuvToRgbaAndLuma(uint3 tid : SV_DispatchThreadID)
        {
            uint w = (uint)g_i.x;
            uint h = (uint)g_i.y;
            if (tid.x >= w || tid.y >= h)
                return;

            int2 p = int2(tid.xy);
            float y = g_texY.Load(int3(p, 0));
            float u;
            float v;
            if (g_i.z == 2)
            {
                u = g_texUV.Load(int3(p >> 1, 0)).x;
                v = g_texV.Load(int3(p >> 1, 0));
            }
            else
            {
                float2 uv = g_texUV.Load(int3(p >> 1, 0));
                u = uv.x;
                v = uv.y;
            }

            float3 rgb = (g_i.z == 1) ? P010ToSdrRgb(y, u, v) : Yuv709ToRgb(y, u, v);
            g_outRgba[p] = float4(rgb, 1.0f);

            if ((p.x & 1) == 0 && (p.y & 1) == 0)
            {
                float luma = dot(rgb, float3(0.299f, 0.587f, 0.114f));
                g_outLuma[p >> 1] = luma;
            }
        }

        Texture2D<float> g_lumaSrc : register(t0);
        RWTexture2D<unorm float> g_lumaDst : register(u0);

        [numthreads(8, 8, 1)]
        void CS_DownsampleLuma(uint3 tid : SV_DispatchThreadID)
        {
            if ((int)tid.x >= g_i.x || (int)tid.y >= g_i.y)
                return;
            int2 maxCoord = int2(g_i.z - 1, g_i.w - 1);
            int2 base = int2(tid.xy) * 2;
            float s0 = g_lumaSrc.Load(int3(min(base + int2(0, 0), maxCoord), 0));
            float s1 = g_lumaSrc.Load(int3(min(base + int2(1, 0), maxCoord), 0));
            float s2 = g_lumaSrc.Load(int3(min(base + int2(0, 1), maxCoord), 0));
            float s3 = g_lumaSrc.Load(int3(min(base + int2(1, 1), maxCoord), 0));
            g_lumaDst[int2(tid.xy)] = (s0 + s1 + s2 + s3) * 0.25f;
        }

        Texture2D<float> g_currLuma : register(t0);
        Texture2D<float> g_prevLuma : register(t1);
        RWStructuredBuffer<uint> g_madBuf : register(u0);
        groupshared uint s_madSum;

        [numthreads(16, 16, 1)]
        void CS_ComputeMad(uint3 tid : SV_DispatchThreadID, uint gidx : SV_GroupIndex)
        {
            if (gidx == 0)
                s_madSum = 0;
            GroupMemoryBarrierWithGroupSync();

            if ((int)tid.x < g_i.x && (int)tid.y < g_i.y)
            {
                float c = g_currLuma.Load(int3(tid.xy, 0));
                float p = g_prevLuma.Load(int3(tid.xy, 0));
                uint diff = (uint)round(abs(c - p) * 255.0f);
                InterlockedAdd(s_madSum, diff);
            }

            GroupMemoryBarrierWithGroupSync();
            if (gidx == 0 && s_madSum > 0)
                InterlockedAdd(g_madBuf[0], s_madSum);
        }

        Texture2D<float2> g_coarseFlow : register(t2);
        RWTexture2D<float2> g_outFlow : register(u0);

        float PatchZsad(int2 cCoord, float2 pCoordFloat, int2 maxCoord, float meanC, out float rawSad)
        {
            float2 invSize = float2(1.0f / (maxCoord.x + 1), 1.0f / (maxCoord.y + 1));
            float pVals[9];
            float sumP = 0.0f;
            int idx = 0;
            [unroll]
            for (int oy = -1; oy <= 1; oy++)
            {
                [unroll]
                for (int ox = -1; ox <= 1; ox++)
                {
                    float2 pUv = (pCoordFloat + float2(ox + 0.5f, oy + 0.5f)) * invSize;
                    float pv = g_prevLuma.SampleLevel(g_linearClamp, pUv, 0);
                    pVals[idx++] = pv;
                    sumP += pv;
                }
            }

            float meanP = sumP / 9.0f;
            float zsad = 0.0f;
            rawSad = 0.0f;
            idx = 0;
            [unroll]
            for (int oy2 = -1; oy2 <= 1; oy2++)
            {
                [unroll]
                for (int ox2 = -1; ox2 <= 1; ox2++)
                {
                    int2 cc = clamp(cCoord + int2(ox2, oy2), int2(0, 0), maxCoord);
                    float cv = g_currLuma.Load(int3(cc, 0));
                    float pv = pVals[idx++];
                    rawSad += abs(cv - pv);
                    zsad += abs((cv - meanC) - (pv - meanP));
                }
            }
            return zsad;
        }

        [numthreads(8, 8, 1)]
        void CS_OpticalFlowLevel(uint3 tid : SV_DispatchThreadID)
        {
            int w = g_i.x;
            int h = g_i.y;
            if ((int)tid.x >= w || (int)tid.y >= h)
                return;

            int2 maxCoord = int2(w - 1, h - 1);
            int2 cCoord = int2(tid.xy);

            float sumC = 0.0f;
            float minC = 1.0f;
            float maxC = 0.0f;
            [unroll]
            for (int oy = -1; oy <= 1; oy++)
            {
                [unroll]
                for (int ox = -1; ox <= 1; ox++)
                {
                    float cv = g_currLuma.Load(int3(clamp(cCoord + int2(ox, oy), int2(0, 0), maxCoord), 0));
                    sumC += cv;
                    minC = min(minC, cv);
                    maxC = max(maxC, cv);
                }
            }
            float meanC = sumC / 9.0f;
            float patchRange = maxC - minC;

            float zeroRaw;
            float zeroZsad = PatchZsad(cCoord, float2(cCoord), maxCoord, meanC, zeroRaw);
            // Static / flat-region gate: sky, asphalt, and static HUD stay locked at zero motion.
            if (patchRange < 0.025f || zeroRaw < 0.030f)
            {
                g_outFlow[cCoord] = float2(0.0f, 0.0f);
                return;
            }

            float pixelStep = g_f.x;
            float2 priorFullPx = float2(0.0f, 0.0f);
            if (g_i.w != 0)
            {
                float2 uv = (float2(tid.xy) + 0.5f) / float2(w, h);
                priorFullPx = g_coarseFlow.SampleLevel(g_linearClamp, uv, 0);
            }

            float2 priorLevelPx = priorFullPx / pixelStep;
            int radius = g_i.z;
            float dummyRaw;
            float bestZsad = PatchZsad(cCoord, float2(cCoord) - priorLevelPx, maxCoord, meanC, dummyRaw);
            float bestCost = bestZsad;
            float2 bestDelta = float2(0.0f, 0.0f);

            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;
                    float2 cand = priorLevelPx + float2(dx, dy);
                    float zsad = PatchZsad(cCoord, float2(cCoord) - cand, maxCoord, meanC, dummyRaw);
                    float cost = zsad + 0.005f * (abs(dx) + abs(dy));
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestZsad = zsad;
                        bestDelta = float2(dx, dy);
                    }
                }
            }

            // Require clear improvement over zero motion before accepting non-zero flow.
            if (bestZsad >= zeroZsad * 0.78f - 0.015f)
            {
                g_outFlow[cCoord] = float2(0.0f, 0.0f);
                return;
            }

            float2 bestLevelPx = priorLevelPx + bestDelta;
            float sadL = PatchZsad(cCoord, float2(cCoord) - (bestLevelPx + float2(-0.5f, 0.0f)), maxCoord, meanC, dummyRaw);
            float sadR = PatchZsad(cCoord, float2(cCoord) - (bestLevelPx + float2(0.5f, 0.0f)), maxCoord, meanC, dummyRaw);
            float denomX = 2.0f * (sadL + sadR - 2.0f * bestZsad);
            if (denomX > 1e-4f)
                bestLevelPx.x += clamp(0.5f * (sadL - sadR) / denomX, -0.5f, 0.5f);

            float sadU = PatchZsad(cCoord, float2(cCoord) - (bestLevelPx + float2(0.0f, -0.5f)), maxCoord, meanC, dummyRaw);
            float sadD = PatchZsad(cCoord, float2(cCoord) - (bestLevelPx + float2(0.0f, 0.5f)), maxCoord, meanC, dummyRaw);
            float denomY = 2.0f * (sadU + sadD - 2.0f * bestZsad);
            if (denomY > 1e-4f)
                bestLevelPx.y += clamp(0.5f * (sadU - sadD) / denomY, -0.5f, 0.5f);

            g_outFlow[cCoord] = clamp(bestLevelPx * pixelStep, -48.0f, 48.0f);
        }

        Texture2D<float2> g_rawFlowFwd : register(t0);
        Texture2D<float2> g_rawFlowBwd : register(t1);
        Texture2D<float> g_guideLuma : register(t2);
        Texture2D<float4> g_prevSmoothFlow : register(t3);
        RWTexture2D<float4> g_smoothFlow : register(u0);

        [numthreads(8, 8, 1)]
        void CS_FlowSmooth(uint3 tid : SV_DispatchThreadID)
        {
            int w = g_i.x;
            int h = g_i.y;
            if ((int)tid.x >= w || (int)tid.y >= h)
                return;

            int2 maxCoord = int2(w - 1, h - 1);
            int2 p = int2(tid.xy);

            // 3x3 outlier suppression (component median via min/max trim) + edge-aware bilateral smooth
            float xs[9];
            float ys[9];
            float ls[9];
            int k = 0;
            float maxDiff = 0.0f;
            float2 centerFlow = g_rawFlowFwd.Load(int3(p, 0));
            [unroll]
            for (int oy = -1; oy <= 1; oy++)
            {
                [unroll]
                for (int ox = -1; ox <= 1; ox++)
                {
                    int2 q = clamp(p + int2(ox, oy), int2(0, 0), maxCoord);
                    float2 f = g_rawFlowFwd.Load(int3(q, 0));
                    xs[k] = f.x;
                    ys[k] = f.y;
                    ls[k] = g_guideLuma.Load(int3(q, 0));
                    maxDiff = max(maxDiff, length(f - centerFlow));
                    k++;
                }
            }

            // Simple bubble sort up to median (element 4)
            [unroll]
            for (int a = 0; a < 5; a++)
            {
                for (int b = a + 1; b < 9; b++)
                {
                    if (xs[b] < xs[a]) { float t = xs[a]; xs[a] = xs[b]; xs[b] = t; }
                    if (ys[b] < ys[a]) { float t = ys[a]; ys[a] = ys[b]; ys[b] = t; }
                }
            }
            float2 medFlow = float2(xs[4], ys[4]);

            float centerL = ls[4];
            float2 sum = medFlow;
            float wSum = 1.0f;
            [unroll]
            for (int i = 0; i < 9; i++)
            {
                if (i == 4) continue;
                int oy2 = (i / 3) - 1;
                int ox2 = (i % 3) - 1;
                int2 q = clamp(p + int2(ox2, oy2), int2(0, 0), maxCoord);
                float2 f = g_rawFlowFwd.Load(int3(q, 0));
                float dL = abs(ls[i] - centerL);
                float dF = length(f - medFlow);
                float weight = exp(-dL * 20.0f - dF * 0.25f);
                sum += f * weight;
                wSum += weight;
            }
            float2 smoothFwd = sum / max(wSum, 1e-4f);
            if (g_i.z != 0)
            {
                float2 prevF = g_prevSmoothFlow.Load(int3(p, 0)).xy;
                if (length(smoothFwd - prevF) < max(6.0f, length(smoothFwd) * 0.6f))
                    smoothFwd = lerp(prevF, smoothFwd, 0.72f);
            }

            // Forward-backward consistency & divergence -> suppress true occlusions/spikes without killing clean object edges
            float2 uv = (float2(p) + 0.5f) / float2(w, h);
            float2 invVideo = float2(g_f.x, g_f.y);
            float2 bwdAtTarget = g_rawFlowBwd.SampleLevel(g_linearClamp, saturate(uv - smoothFwd * invVideo), 0);
            float fbErr = length(smoothFwd + bwdAtTarget);
            float flowMag = length(smoothFwd);
            float fbTol = max(6.0f, flowMag * 1.0f);
            float divTol = max(14.0f, flowMag * 1.75f);
            float conf = saturate(1.0f - max(0.0f, fbErr - 2.0f) / fbTol)
                       * saturate(1.0f - max(0.0f, maxDiff - 4.0f) / divTol);

            g_smoothFlow[p] = float4(smoothFwd, conf, saturate(fbErr / 6.0f));
        }

        struct VsOut
        {
            float4 pos : SV_Position;
            float2 uv : TEXCOORD0;
        };

        VsOut VS_FullscreenQuad(uint id : SV_VertexID)
        {
            VsOut o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.uv = uv;
            o.pos = float4(uv.x * 2.0f - 1.0f, 1.0f - uv.y * 2.0f, 0.0f, 1.0f);
            return o;
        }

        Texture2D<float4> g_primaryTex : register(t0);
        Texture2D<float4> g_secondaryTex : register(t1);
        Texture2D<float4> g_flowTex : register(t2);

        float3 SampleFrameGen(float2 uv)
        {
            if (g_mode == 0)
                return g_primaryTex.SampleLevel(g_linearClamp, uv, 0).rgb;

            float4 flowData = g_flowTex.SampleLevel(g_linearClamp, uv, 0);
            float2 flowUv = flowData.xy * float2(g_invW, g_invH);
            float conf = flowData.z;

            if (g_mode == 1)
            {
                float2 srcUv = saturate(uv - flowUv * g_alpha);
                float3 warped = g_primaryTex.SampleLevel(g_linearClamp, srcUv, 0).rgb;
                float3 direct = g_primaryTex.SampleLevel(g_linearClamp, uv, 0).rgb;
                float border = saturate(min(min(srcUv.x, 1.0f - srcUv.x), min(srcUv.y, 1.0f - srcUv.y)) * 40.0f);
                return lerp(direct, warped, border * conf);
            }

            float2 uvPrev = saturate(uv - flowUv * g_alpha);
            float2 uvCurr = saturate(uv + flowUv * (1.0f - g_alpha));
            float3 cPrev = g_primaryTex.SampleLevel(g_linearClamp, uvPrev, 0).rgb;
            float3 cCurr = g_secondaryTex.SampleLevel(g_linearClamp, uvCurr, 0).rgb;
            float3 warpedBlend = lerp(cPrev, cCurr, g_alpha);
            float3 nearestReal = g_alpha < 0.5f
                ? g_primaryTex.SampleLevel(g_linearClamp, uv, 0).rgb
                : g_secondaryTex.SampleLevel(g_linearClamp, uv, 0).rgb;
            float agree = saturate(1.0f - length(cPrev - cCurr) * 2.2f) * conf;
            return lerp(nearestReal, warpedBlend, agree);
        }

        float3 ApplyCas(float2 uv, float3 e)
        {
            float3 b = SampleFrameGen(uv + float2(0.0f, -g_invH));
            float3 d = SampleFrameGen(uv + float2(-g_invW, 0.0f));
            float3 f = SampleFrameGen(uv + float2(g_invW, 0.0f));
            float3 h = SampleFrameGen(uv + float2(0.0f, g_invH));

            float3 mn = min(min(b, d), min(f, min(h, e)));
            float3 mx = max(max(b, d), max(f, max(h, e)));
            float3 amp = saturate(min(mn, 1.0f - mx) / max(mx, 1e-4f));
            amp = sqrt(amp);
            float peak = -1.0f / lerp(8.0f, 5.0f, g_sharpness);
            float3 w = amp * peak;
            float3 rcpW = 1.0f / (1.0f + 4.0f * w);
            return saturate((b * w + d * w + f * w + h * w + e) * rcpW);
        }

        float4 PS_PresentFrameGenCas(VsOut i) : SV_Target
        {
            float3 color = SampleFrameGen(i.uv);
            if (g_sharpness > 0.005f)
                color = ApplyCas(i.uv, color);
            return float4(color, 1.0f);
        }
        """;
}
