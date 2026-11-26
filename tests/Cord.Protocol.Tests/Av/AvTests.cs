using Cord.Protocol.Av;
using Cord.Protocol.Stream;
using static Cord.Protocol.Tests.Support.TestBytes;

namespace Cord.Protocol.Tests.Av;

public class AvTests
{
    // Parity units were produced by a reference implementation for these source units.
    [Theory]
    [InlineData(3, 2, new[] { "a386c70d4b4e0bc18584e9d465afae93", "1c29f0dba00b210a2087760c265ee79d" })]
    [InlineData(5, 3, new[] { "2b664531624d243ba53d3a8f1d5c01f2", "46b986eb2617db93c082287788a35bb5", "7313b5d00e10ef2eefd37738cc773d8b" })]
    public void CauchyFec_Encode_MatchesReference(int k, int m, string[] parity)
    {
        var units = Enumerable.Range(0, k + m)
            .Select(u => (Memory<byte>)(u < k ? Sequence(16, i => (u * 31) + (i * 7) + 1) : new byte[16]))
            .ToList();

        CauchyFec.Encode(units, k, m);

        for (var p = 0; p < m; p++)
            Assert.Equal(Hex(parity[p]), units[k + p].ToArray());
    }

    [Fact]
    public void CauchyFec_Decode_RecoversAnyErasureSet()
    {
        const int k = 5, m = 3;
        var original = Enumerable.Range(0, k).Select(u => Sequence(24, i => (u * 17) + (i * 3))).ToArray();

        foreach (var erased in new[] { new[] { 0 }, new[] { 1, 3 }, new[] { 0, 2, 4 }, new[] { 4, 5, 6 } })
        {
            var units = original.Select(u => (Memory<byte>)u.ToArray()).Concat(Enumerable.Range(0, m).Select(_ => (Memory<byte>)new byte[24])).ToList();
            CauchyFec.Encode(units, k, m);
            var present = Enumerable.Range(0, k + m).Select(u => !erased.Contains(u)).ToList();
            foreach (var u in erased)
                units[u].Span.Clear();

            Assert.True(CauchyFec.Decode(units, present, k, m));
            for (var u = 0; u < k; u++)
                Assert.Equal(original[u], units[u].ToArray());
        }
    }

    [Fact]
    public void CauchyFec_Decode_FailsWithTooFewUnits()
    {
        var units = Enumerable.Range(0, 5).Select(_ => (Memory<byte>)new byte[8]).ToList();
        Assert.False(CauchyFec.Decode(units, [false, false, true, true, false], 3, 2));
    }

    [Fact]
    public void AvPacket_ParsesVideoHeader()
    {
        var datagram = AvTestPackets.Video(0x1234, 0x0042, unitIndex: 5, total: 12, fec: 2, adaptive: 1, [0xAA]);

        Assert.True(AvPacket.TryParse(datagram, out var packet));
        Assert.True(packet.IsVideo);
        Assert.Equal(0x1234, packet.PacketIndex);
        Assert.Equal(0x0042, packet.FrameIndex);
        Assert.Equal(5, packet.UnitIndex);
        Assert.Equal(12, packet.UnitsTotal);
        Assert.Equal(2, packet.FecUnits);
        Assert.Equal(10, packet.SourceUnits);
        Assert.Equal(1, packet.AdaptiveStreamIndex);
        Assert.Equal(0x15, packet.PayloadOffset);
    }

    [Fact]
    public void AvPacket_ParsesAudioHeader()
    {
        var datagram = AvTestPackets.Audio(100, source: 1, fec: 2, [[1, 2], [3, 4], [5, 6]], haptics: true);

        Assert.True(AvPacket.TryParse(datagram, out var packet));
        Assert.False(packet.IsVideo);
        Assert.Equal(3, packet.UnitsTotal);
        Assert.Equal(2, packet.UnitSize);
        Assert.Equal(1, packet.SourceUnits);
        Assert.Equal(2, packet.FecUnits);
        Assert.True(packet.IsHaptics);
        Assert.Equal(0x14, packet.PayloadOffset);
    }

    [Fact]
    public void VideoReceiver_RebuildsFramesAndUsesFec()
    {
        var sink = new VideoSink();
        var profiles = new[] { new VideoStreamProfile(1280, 720, [0, 0, 0, 1, 0x67]) };
        var receiver = new VideoReceiver(profiles, sink);

        var first = Sequence(100, i => i);
        Feed(receiver, frameIndex: 1, first, sourceUnits: 4, fecUnits: 2, drop: []);
        var second = Sequence(77, i => 200 - i);
        Feed(receiver, frameIndex: 2, second, sourceUnits: 4, fecUnits: 2, drop: [1, 3]);

        Assert.Equal(2, sink.Frames.Count);
        Assert.Equal(first, sink.Frames[0].Frame);
        Assert.Equal(profiles[0].Header, sink.Frames[0].Header);
        Assert.False(sink.Frames[0].Recovered);
        Assert.Equal(second, sink.Frames[1].Frame);
        Assert.Null(sink.Frames[1].Header);
        Assert.True(sink.Frames[1].Recovered);
        Assert.Empty(sink.Corrupt);
    }

    [Fact]
    public void VideoReceiver_ReportsMissingAndUnrecoverableFrames()
    {
        var sink = new VideoSink();
        var receiver = new VideoReceiver([new VideoStreamProfile(1280, 720, [1])], sink);

        Feed(receiver, 1, Sequence(40, i => i), 2, 1, drop: []);
        Feed(receiver, 4, Sequence(40, i => i), 2, 1, drop: [0, 1]);
        Feed(receiver, 5, Sequence(40, i => i), 2, 1, drop: []);

        Assert.Equal([(2, 3), (4, 4)], sink.Corrupt);
        Assert.Equal(2, sink.Frames.Count);
    }

    [Fact]
    public void VideoReceiver_DropsBrokenDeltaFramesUntilNextKeyframeWhenEnabled()
    {
        var sink = new VideoSink();
        var header = new byte[] { 0, 0, 0, 1, 0x67 };
        var receiver = new VideoReceiver([new VideoStreamProfile(1280, 720, header)], sink, dropCorruptUntilKeyframe: true, isHevc: false);

        var idr = new byte[] { 0, 0, 0, 1, 0x65, 1, 2, 3, 4, 5, 6, 7 };
        var delta = new byte[] { 0, 0, 0, 1, 0x41, 9, 8, 7, 6, 5, 4, 3 };

        Feed(receiver, 1, idr, 2, 1, drop: []);
        Feed(receiver, 2, delta, 2, 1, drop: [0, 1]); // corrupt frame 2
        Feed(receiver, 3, delta, 2, 1, drop: []);     // dependent P-frame dropped cleanly
        Feed(receiver, 4, idr, 2, 1, drop: []);       // resumes on clean IDR!

        Assert.Equal(2, sink.Frames.Count);
        Assert.Equal(1, receiver.FramesDroppedForRecovery);
        Assert.Equal(header, sink.Frames[1].Header);
    }

    [Fact]
    public void PacketStats_TracksLossPercent()
    {
        var stats = new PacketStats();
        stats.Record(1);
        stats.Record(2);
        stats.Record(5); // skipped 3 and 4 => 2 lost, 3 received

        var (received, lost) = stats.TakeSnapshot();
        Assert.Equal(3, received);
        Assert.Equal(2, lost);
        Assert.InRange(stats.LastLossPercent, 39.9, 40.1);
    }

    [Fact]
    public void AudioReceiver_DeliversEachFrameOnceAndFillsGapsFromRedundancy()
    {
        var sink = new AudioSink();
        var receiver = new AudioReceiver(sink);

        foreach (var frame in new ushort[] { 10, 11, 13 })
        {
            var datagram = AvTestPackets.Audio(frame, 1, 2, [[(byte)frame], [(byte)(frame - 2)], [(byte)(frame - 1)]]);
            Assert.True(AvPacket.TryParse(datagram, out var packet));
            receiver.OnPacket(packet, datagram.AsSpan(packet.PayloadOffset));
        }

        Assert.Equal(new ushort[] { 8, 9, 10, 11, 12, 13 }, sink.Frames.Select(f => f.Index));
        Assert.All(sink.Frames, f => Assert.Equal((byte)f.Index, f.Data[0]));
    }

    [Fact]
    public void AudioReceiver_KeepsPcmHapticsIndependentAndRecoversRedundantUnits()
    {
        var sink = new AudioSink();
        var receiver = new AudioReceiver(sink);
        var pcm = new byte[] { 0x00, 0x80, 0xFF, 0x7F, 0x34, 0x12, 0xCC, 0xED };
        var packets = new[]
        {
            AvTestPackets.Audio(10, 1, 0, [[0xAA]]),
            AvTestPackets.Audio(10, 1, 0, [pcm], haptics: true),
            AvTestPackets.Audio(10, 1, 0, [pcm], haptics: true),
            AvTestPackets.Audio(12, 1, 1, [pcm, pcm], haptics: true),
            AvTestPackets.Audio(11, 1, 0, [[0xBB]]),
        };

        foreach (var datagram in packets)
        {
            Assert.True(AvPacket.TryParse(datagram, out var packet));
            receiver.OnPacket(packet, datagram.AsSpan(packet.PayloadOffset));
        }

        Assert.Equal(new ushort[] { 10, 10, 11, 12, 11 }, sink.Frames.Select(f => f.Index));
        Assert.Equal([false, true, true, true, false], sink.Frames.Select(f => f.Haptics));
        Assert.All(sink.Frames.Where(f => f.Haptics), frame => Assert.Equal(pcm, frame.Data));
        Assert.Equal(new byte[] { 0xAA }, sink.Frames[0].Data);
        Assert.Equal(new byte[] { 0xBB }, sink.Frames[4].Data);
    }

    private static void Feed(VideoReceiver receiver, ushort frameIndex, byte[] frame, int sourceUnits, int fecUnits, int[] drop)
    {
        var units = AvTestPackets.EncodeFrame(frame, sourceUnits, fecUnits);
        for (var u = 0; u < units.Length; u++)
        {
            if (drop.Contains(u))
                continue;
            var datagram = AvTestPackets.Video((ushort)u, frameIndex, u, units.Length, fecUnits, 0, units[u]);
            Assert.True(AvPacket.TryParse(datagram, out var packet));
            receiver.OnPacket(packet, datagram.AsSpan(packet.PayloadOffset));
        }
    }

    private sealed class VideoSink : IVideoReceiverSink
    {
        public List<(byte[] Frame, byte[]? Header, bool Recovered)> Frames { get; } = [];
        public List<(int, int)> Corrupt { get; } = [];

        public void OnFrame(ReadOnlyMemory<byte> frame, ReadOnlyMemory<byte>? streamHeader, bool recoveredWithFec) =>
            Frames.Add((frame.ToArray(), streamHeader?.ToArray(), recoveredWithFec));

        public void OnCorruptFrames(ushort first, ushort last) => Corrupt.Add((first, last));
    }

    private sealed class AudioSink : IAudioReceiverSink
    {
        public List<(ushort Index, byte[] Data, bool Haptics)> Frames { get; } = [];

        public void OnAudioFrame(ushort frameIndex, ReadOnlySpan<byte> opusFrame, bool haptics) => Frames.Add((frameIndex, opusFrame.ToArray(), haptics));
    }
}
