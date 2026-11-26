using Hexa.NET.SDL3;

namespace Cord.App.Platform;

/// <summary>Builds the window icon from the PNGs embedded in Assets/Icons.</summary>
internal static class WindowIcon
{
    /// <summary>The 100% scale image; SDL picks the larger alternates on high-DPI displays.</summary>
    public const int BaseSize = 32;

    public static readonly int[] AlternateSizes = [48, 64, 128, 256];

    /// <summary>Returns the icon surface with its alternates, or a null surface if an image cannot be read.</summary>
    public static SDLSurfacePtr Load()
    {
        var icon = LoadPng(BaseSize);
        if (icon.IsNull)
            return icon;
        foreach (var size in AlternateSizes)
        {
            var alternate = LoadPng(size);
            if (alternate.IsNull)
                continue;
            SDL.AddSurfaceAlternateImage(icon, alternate);
            SDL.DestroySurface(alternate);
        }

        return icon;
    }

    private static SDLSurfacePtr LoadPng(int size) => EmbeddedPng.Load($"Icons.cord-{size}.png");
}
