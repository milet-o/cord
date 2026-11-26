using System.Numerics;
using Hexa.NET.ImGui;
using Hexa.NET.SDL3;
using Cord.App.Platform;
using Vortice.Direct3D11;

namespace Cord.App.Ui;

/// <summary>
/// The app logo drawn as an image. Keeps one texture per size and draws the smallest one
/// that is at least as tall as the target.
/// </summary>
internal static class Logo
{
    private static readonly int[] Heights = [64, 128, 256, 512];

    // Width / height of the artwork, used to reserve space when no texture is loaded (tests).
    private const float AspectRatio = 1f;

    private static Texture[] _textures = [];
    private static readonly List<IDisposable> OwnedResources = [];

    /// <summary>Uploads the logo to the host (native D3D11 or SDL_Renderer fallback).</summary>
    public static unsafe void Load(SdlHost host)
    {
        Unload();
        if (host.D3D11 is { } d3d11)
        {
            LoadD3D11(d3d11);
            return;
        }

        if (!host.Renderer.IsNull)
            Load(host.Renderer);
    }

    /// <summary>Uploads the logo to the SDL renderer. SDL frees the textures with the renderer.</summary>
    public static unsafe void Load(SDLRendererPtr renderer)
    {
        Unload();
        var textures = new List<Texture>();
        foreach (var height in Heights)
        {
            var surface = EmbeddedPng.Load($"Images.logo-{height}.png");
            if (surface.IsNull)
                continue;
            var texture = SDL.CreateTextureFromSurface(renderer, surface);
            var size = new Vector2(surface.W, surface.H);
            SDL.DestroySurface(surface);
            if (!texture.IsNull)
                textures.Add(new Texture((nint)texture.Handle, size));
        }

        _textures = [.. textures];
    }

    private static void LoadD3D11(D3D11Host d3d11)
    {
        var textures = new List<Texture>();
        foreach (var height in Heights)
        {
            var surface = EmbeddedPng.Load($"Images.logo-{height}.png");
            if (surface.IsNull)
                continue;
            var size = new Vector2(surface.W, surface.H);
            var created = d3d11.CreateTextureFromSurface(surface);
            SDL.DestroySurface(surface);
            if (created is { } res)
            {
                OwnedResources.Add(res.Srv);
                OwnedResources.Add(res.Texture);
                textures.Add(new Texture(res.Srv.NativePointer, size));
            }
        }

        _textures = [.. textures];
    }

    public static void Unload()
    {
        foreach (var res in OwnedResources)
            res.Dispose();
        OwnedResources.Clear();
        _textures = [];
    }

    /// <summary>Draws the logo <paramref name="height"/> design units tall, optionally centered in the available width.</summary>
    public static unsafe void Draw(float height, bool centered = false)
    {
        var pixels = Theme.Dp(height);
        var texture = _textures.FirstOrDefault(t => t.Size.Y >= pixels, _textures.LastOrDefault());
        var aspect = texture.Handle == 0 ? AspectRatio : texture.Size.X / texture.Size.Y;
        var size = new Vector2(pixels * aspect, pixels);
        if (centered)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (ImGui.GetContentRegionAvail().X - size.X) / 2));

        if (texture.Handle == 0)
            ImGui.Dummy(size);
        else
            ImGui.Image(new ImTextureRef(null, new ImTextureID(texture.Handle)), size);
    }

    private readonly record struct Texture(nint Handle, Vector2 Size);
}
