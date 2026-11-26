using Cord.WebShare;

namespace Cord.WebShare.Tests;

public sealed class AnnexBVideoInspectorTests
{
    [Fact]
    public void H264Fixture_DetectsParameterSetsKeyframeAndCodecString()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "testsrc-320x240.h264"));

        Assert.True(AnnexBVideoInspector.IsKeyframe(bytes, isHevc: false));
        Assert.True(AnnexBVideoInspector.ContainsParameterSets(bytes, isHevc: false));

        var codec = AnnexBVideoInspector.ExtractCodecString(bytes, isHevc: false);
        Assert.StartsWith("avc1.", codec, StringComparison.Ordinal);
        Assert.Equal(11, codec.Length);
    }

    [Fact]
    public void HevcFixture_DetectsParameterSetsAndKeyframe()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "testsrc-320x240.hevc"));

        Assert.True(AnnexBVideoInspector.IsKeyframe(bytes, isHevc: true));
        Assert.True(AnnexBVideoInspector.ContainsParameterSets(bytes, isHevc: true));
        Assert.Equal("hev1.1.6.L120.90", AnnexBVideoInspector.ExtractCodecString(bytes, isHevc: true));
    }

    [Fact]
    public void DeltaSlice_IsNotDetectedAsKeyframe()
    {
        byte[] delta = [0x00, 0x00, 0x00, 0x01, 0x41, 0x9A, 0x11, 0x22];
        Assert.False(AnnexBVideoInspector.IsKeyframe(delta, isHevc: false));
        Assert.False(AnnexBVideoInspector.ContainsParameterSets(delta, isHevc: false));
    }
}
