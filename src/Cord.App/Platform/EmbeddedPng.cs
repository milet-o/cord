using System.Reflection;
using Hexa.NET.SDL3;

namespace Cord.App.Platform;

/// <summary>Decodes PNG images embedded under Assets with SDL.</summary>
internal static unsafe class EmbeddedPng
{
    /// <summary>Returns the image as a surface, or a null surface when it is missing or invalid.</summary>
    public static SDLSurfacePtr Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Cord.App.Assets.{name}");
        if (stream is null)
            return default;
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        fixed (byte* bytes = data)
            return SDL.LoadPNGIO(SDL.IOFromConstMem(bytes, (nuint)data.Length), true);
    }
}
