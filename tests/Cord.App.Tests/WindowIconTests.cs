using Hexa.NET.SDL3;
using Cord.App.Platform;

namespace Cord.App.Tests;

public sealed unsafe class WindowIconTests
{
    [Fact]
    public void EmbeddedIconLoadsWithEveryAlternateSize()
    {
        var icon = WindowIcon.Load();
        Assert.False(icon.IsNull);
        try
        {
            int count;
            var images = SDL.GetSurfaceImages(icon, &count);
            var sizes = new List<(int W, int H)>();
            for (var i = 0; i < count; i++)
                sizes.Add((images[i]->W, images[i]->H));
            SDL.Free(images);

            Assert.Equal([WindowIcon.BaseSize, .. WindowIcon.AlternateSizes], sizes.Select(s => s.W));
            Assert.All(sizes, s => Assert.Equal(s.W, s.H));
        }
        finally
        {
            SDL.DestroySurface(icon);
        }
    }
}
