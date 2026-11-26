using System.Buffers.Binary;
using Hexa.NET.SDL3;
using Cord.App.Audio;

namespace Cord.App.Tests;

public sealed unsafe class HapticsOutputTests : IDisposable
{
    private readonly SDLAudioStreamPtr _stream = HapticsOutput.CreateStream();

    public HapticsOutputTests() => Assert.False(_stream.IsNull);

    [Fact]
    public void Conversion_ResamplesTo48KhzAndRoutesOnlyToActuators()
    {
        var pcm = ConstantPcm(3000, 12000, -6000);
        Assert.True(HapticsOutput.QueueFrame(_stream, pcm));
        Assert.True(SDL.FlushAudioStream(_stream));

        var result = Drain();

        Assert.Equal(48000 * 4, result.Length);
        for (var i = 0; i < 48000; i++)
        {
            Assert.Equal(0, result[4 * i]);
            Assert.Equal(0, result[(4 * i) + 1]);
            if (i is >= 1024 and < 46976)
            {
                Assert.InRange(result[(4 * i) + 2], 11998, 12002);
                Assert.InRange(result[(4 * i) + 3], -6002, -5998);
            }
        }
    }

    [Fact]
    public void Conversion_PreservesContinuityAcrossPackets()
    {
        var pcm = new byte[3000 * 4];
        for (var i = 0; i < 3000; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(4 * i), (short)(Math.Sin(2 * Math.PI * 110 * i / 3000) * 12000));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((4 * i) + 2), (short)(Math.Sin(2 * Math.PI * 220 * i / 3000) * 6000));
        }
        Assert.True(HapticsOutput.QueueFrame(_stream, pcm));
        Assert.True(SDL.FlushAudioStream(_stream));
        var expected = Drain();
        SDL.ClearAudioStream(_stream);

        var result = new List<short>();
        for (var offset = 0; offset < pcm.Length; offset += 120)
        {
            Assert.True(HapticsOutput.QueueFrame(_stream, pcm.AsSpan(offset, 120)));
            result.AddRange(Drain());
        }
        Assert.True(SDL.FlushAudioStream(_stream));
        result.AddRange(Drain());

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void QueueFrame_RejectsIncompleteStereoPcm(int length)
    {
        Assert.False(HapticsOutput.QueueFrame(_stream, new byte[length]));
        Assert.Equal(0, SDL.GetAudioStreamQueued(_stream));
    }

    [Fact]
    public void QueueFrame_DiscardsStaleHapticsBeyond80Milliseconds()
    {
        Assert.True(HapticsOutput.QueueFrame(_stream, ConstantPcm(300, 1000, -1000)));
        Assert.Equal(2400, SDL.GetAudioStreamQueued(_stream));

        Assert.True(HapticsOutput.QueueFrame(_stream, ConstantPcm(30, 2000, -2000)));

        Assert.Equal(240, SDL.GetAudioStreamQueued(_stream));
    }

    [Fact]
    public void DisabledOutput_DropsFramesWithoutOpeningDefaultAudio()
    {
        using var output = new HapticsOutput();
        output.SetEnabled(false);
        output.Play(ConstantPcm(30, short.MaxValue, short.MinValue));
        output.Refresh();
        Assert.False(output.Available);
    }

    public void Dispose() => SDL.DestroyAudioStream(_stream);

    private short[] Drain()
    {
        var result = new short[SDL.GetAudioStreamAvailable(_stream) / sizeof(short)];
        fixed (short* data = result)
            Assert.Equal(result.Length * sizeof(short), SDL.GetAudioStreamData(_stream, data, result.Length * sizeof(short)));
        return result;
    }

    private static byte[] ConstantPcm(int frames, short left, short right)
    {
        var pcm = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(4 * i), left);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((4 * i) + 2), right);
        }
        return pcm;
    }
}
