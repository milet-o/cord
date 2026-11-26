using System.Runtime.InteropServices;
using System.Text;

namespace Cord.App.Video;

/// <summary>Compiles HLSL shaders at runtime through Windows d3dcompiler_47.dll.</summary>
internal static unsafe partial class D3D11ShaderCompiler
{
    private const uint D3DCompileOptimizationLevel3 = 1u << 15;

    public static byte[] Compile(string hlslSource, string entryPoint, string target)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("D3D11 shader compilation is supported only on Windows.");

        var sourceBytes = Encoding.UTF8.GetBytes(hlslSource);
        nint codeBlob = 0;
        nint errorBlob = 0;
        try
        {
            int hr;
            fixed (byte* srcPtr = sourceBytes)
            {
                hr = D3DCompile(
                    srcPtr,
                    (nuint)sourceBytes.Length,
                    "cord_shaders.hlsl",
                    null,
                    0,
                    entryPoint,
                    target,
                    D3DCompileOptimizationLevel3,
                    0,
                    out codeBlob,
                    out errorBlob);
            }

            if (hr < 0 || codeBlob == 0)
            {
                var message = ReadBlobString(errorBlob);
                throw new InvalidOperationException($"HLSL compile failed ({entryPoint}/{target}, hr=0x{hr:X8}): {message}");
            }

            var ptr = GetBufferPointer(codeBlob);
            var size = (int)GetBufferSize(codeBlob);
            var bytecode = new byte[size];
            Marshal.Copy(ptr, bytecode, 0, size);
            return bytecode;
        }
        finally
        {
            if (codeBlob != 0)
                Marshal.Release(codeBlob);
            if (errorBlob != 0)
                Marshal.Release(errorBlob);
        }
    }

    private static string ReadBlobString(nint blob)
    {
        if (blob == 0)
            return "unknown error";
        var ptr = GetBufferPointer(blob);
        var size = (int)GetBufferSize(blob);
        return ptr == 0 || size <= 0 ? "unknown error" : Marshal.PtrToStringUTF8(ptr, size) ?? "unknown error";
    }

    private static nint GetBufferPointer(nint blob)
    {
        // ID3DBlob vtable: 0 QueryInterface, 1 AddRef, 2 Release, 3 GetBufferPointer, 4 GetBufferSize
        var vtable = *(nint**)blob;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint>)vtable[3];
        return fn(blob);
    }

    private static nuint GetBufferSize(nint blob)
    {
        var vtable = *(nint**)blob;
        var fn = (delegate* unmanaged[Stdcall]<nint, nuint>)vtable[4];
        return fn(blob);
    }

    [LibraryImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int D3DCompile(
        byte* pSrcData,
        nuint srcDataSize,
        string? pSourceName,
        nint* pDefines,
        nint pInclude,
        string pEntrypoint,
        string pTarget,
        uint flags1,
        uint flags2,
        out nint ppCode,
        out nint ppErrorMsgs);
}
