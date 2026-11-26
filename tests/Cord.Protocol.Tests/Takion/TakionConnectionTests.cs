using System.Buffers.Binary;
using System.Collections.Concurrent;
using Cord.Protocol.Crypto;
using Cord.Protocol.Takion;
using static Cord.Protocol.Tests.Support.TestBytes;

namespace Cord.Protocol.Tests.Takion;

public class TakionConnectionTests
{
    private static readonly byte[] Secret = Sequence(32, i => i);
    private static readonly byte[] HandshakeKey = Sequence(16, i => 0x80 + i);

    [Fact]
    public async Task Handshake_ThenExchangesReliableData()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var console = new FakeTakionConsole();
        var handler = new RecordingHandler();
        var accept = console.AcceptAsync(timeout.Token);

        await using var connection = await TakionConnection.ConnectAsync(console.EndPoint, handler, dontFragment: false, timeout.Token);
        await accept;

        await connection.SendMessageAsync(TakionDataType.Protobuf, new byte[] { 1, 2, 3 }, cancellationToken: timeout.Token);
        var data = await console.ReceiveControlAsync(TakionChunkType.Data, timeout.Token);
        Assert.Equal(TakionConnection.MessageChannel, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4)));
        Assert.Equal((byte)TakionDataType.Protobuf, data[8]);
        Assert.Equal(new byte[] { 1, 2, 3 }, data[9..]);

        await console.SendDataAsync(TakionDataType.Rumble, [9, 8, 7], timeout.Token);
        var ack = await console.ReceiveControlAsync(TakionChunkType.DataAck, timeout.Token);
        Assert.Equal(FakeTakionConsole.ConsoleTag, BinaryPrimitives.ReadUInt32BigEndian(ack));

        var (type, received) = await handler.Data.Reader.ReadAsync(timeout.Token);
        Assert.Equal(TakionDataType.Rumble, type);
        Assert.Equal(new byte[] { 9, 8, 7 }, received);
    }

    [Fact]
    public async Task SilentConsole_IsReportedAsClosedOnce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var console = new FakeTakionConsole();
        var handler = new RecordingHandler();
        var accept = console.AcceptAsync(timeout.Token);
        await using var connection = await TakionConnection.ConnectAsync(console.EndPoint, handler, false, timeout.Token);
        await accept;

        connection.SilenceTimeout = TimeSpan.FromMilliseconds(500);
        while (handler.Closed.IsEmpty)
            await Task.Delay(50, timeout.Token);
        await Task.Delay(600, timeout.Token);

        Assert.IsType<TimeoutException>(Assert.Single(handler.Closed));
    }

    [Fact]
    public async Task ConsoleThatKeepsSending_IsNotReportedAsSilent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var console = new FakeTakionConsole();
        var handler = new RecordingHandler();
        var accept = console.AcceptAsync(timeout.Token);
        await using var connection = await TakionConnection.ConnectAsync(console.EndPoint, handler, false, timeout.Token);
        await accept;

        connection.SilenceTimeout = TimeSpan.FromMilliseconds(600);
        for (var i = 0u; i < 10; i++)
        {
            await console.SendDataAsync(FakeTakionConsole.ConsoleTag + i, TakionDataType.Protobuf, [1], timeout.Token);
            await Task.Delay(150, timeout.Token);
        }

        Assert.Empty(handler.Closed);
    }

    [Fact]
    public async Task Data_IsDeliveredInOrder()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var console = new FakeTakionConsole();
        var handler = new RecordingHandler();
        var accept = console.AcceptAsync(timeout.Token);
        await using var connection = await TakionConnection.ConnectAsync(console.EndPoint, handler, false, timeout.Token);
        await accept;

        await console.SendDataAsync(FakeTakionConsole.ConsoleTag + 1, TakionDataType.Protobuf, [2], timeout.Token);
        await console.SendDataAsync(FakeTakionConsole.ConsoleTag, TakionDataType.Protobuf, [1], timeout.Token);

        Assert.Equal(new byte[] { 1 }, (await handler.Data.Reader.ReadAsync(timeout.Token)).Data);
        Assert.Equal(new byte[] { 2 }, (await handler.Data.Reader.ReadAsync(timeout.Token)).Data);
    }

    [Fact]
    public async Task UnacknowledgedData_IsRetransmitted()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var console = new FakeTakionConsole();
        var accept = console.AcceptAsync(timeout.Token);
        await using var connection = await TakionConnection.ConnectAsync(console.EndPoint, new RecordingHandler(), false, timeout.Token);
        await accept;

        await connection.SendMessageAsync(TakionDataType.Protobuf, new byte[] { 5 }, cancellationToken: timeout.Token);
        var first = await console.ReceiveControlAsync(TakionChunkType.Data, timeout.Token);
        var second = await console.ReceiveControlAsync(TakionChunkType.Data, timeout.Token);
        Assert.Equal(first, second);

        await console.AckAsync(FakeTakionConsole.SequenceOf(first), timeout.Token);
    }

    [Fact]
    public async Task LargeMessage_IsFragmented()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var console = new FakeTakionConsole();
        var accept = console.AcceptAsync(timeout.Token);
        await using var connection = await TakionConnection.ConnectAsync(console.EndPoint, new RecordingHandler(), false, timeout.Token);
        await accept;

        var message = Sequence(25, i => i);
        await connection.SendMessageAsync(TakionDataType.Protobuf, message, maxChunkData: 10, timeout.Token);

        var chunks = new List<(byte Flags, byte[] Payload)>();
        while (chunks.Count < 3)
        {
            var datagram = await console.ReceiveDatagramAsync(timeout.Token);
            if (TakionPackets.TryParseControl(datagram, out var chunk) && chunk.Type == TakionChunkType.Data)
                chunks.Add((chunk.Flags, chunk.Payload.ToArray()));
        }

        Assert.Equal(new byte[] { 0, 0, 1 }, chunks.Select(c => c.Flags));
        Assert.Equal(message[..10], chunks[0].Payload[9..]);
        Assert.Equal(message[10..21], chunks[1].Payload[8..]);
        Assert.Equal(message[21..], chunks[2].Payload[8..]);
    }

    [Fact]
    public async Task WithCrypt_VerifiesConsolePacketsAndSignsOwn()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var console = new FakeTakionConsole();
        var handler = new RecordingHandler();
        var accept = console.AcceptAsync(timeout.Token);
        await using var connection = await TakionConnection.ConnectAsync(console.EndPoint, handler, false, timeout.Token);
        await accept;

        using var clientLocal = new GkCrypt(GkCrypt.LocalIndex, HandshakeKey, Secret);
        using var clientRemote = new GkCrypt(GkCrypt.RemoteIndex, HandshakeKey, Secret);
        using var consoleLocal = new GkCrypt(GkCrypt.RemoteIndex, HandshakeKey, Secret);
        using var consoleVerifier = new GkCrypt(GkCrypt.LocalIndex, HandshakeKey, Secret);
        connection.SetCrypt(clientLocal, clientRemote);
        console.LocalCrypt = consoleLocal;

        var tampered = TakionPackets.BuildControl(0, TakionChunkType.Data, 1, TakionPackets.BuildData(FakeTakionConsole.ConsoleTag, 1, TakionDataType.Protobuf, [0xEE]));
        await console.SendAsync(tampered, timeout.Token);
        await console.SendDataAsync(TakionDataType.Protobuf, [0x42], timeout.Token);
        var (_, received) = await handler.Data.Reader.ReadAsync(timeout.Token);
        Assert.Equal(new byte[] { 0x42 }, received);

        var ack = await console.ReceiveDatagramAsync(timeout.Token);
        var keyPos = TakionPackets.ReadKeyPosLow(ack);
        Assert.True(TakionPackets.Verify(consoleVerifier, ack, keyPos));

        var video = new byte[0x30];
        video[0] = (byte)TakionPacketType.Video;
        TakionPackets.Sign(consoleLocal, video, console.NextKeyPos(video.Length));
        await console.SendAsync(video, timeout.Token);
        var (datagram, avKeyPos) = await handler.Av.Reader.ReadAsync(timeout.Token);
        Assert.Equal(video, datagram);
        Assert.Equal(2UL, avKeyPos);
    }

    [Fact]
    public async Task Feedback_IsEncryptedAndSigned()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var console = new FakeTakionConsole();
        var accept = console.AcceptAsync(timeout.Token);
        await using var connection = await TakionConnection.ConnectAsync(console.EndPoint, new RecordingHandler(), false, timeout.Token);
        await accept;
        using var clientLocal = new GkCrypt(GkCrypt.LocalIndex, HandshakeKey, Secret);
        using var clientRemote = new GkCrypt(GkCrypt.RemoteIndex, HandshakeKey, Secret);
        using var consoleVerifier = new GkCrypt(GkCrypt.LocalIndex, HandshakeKey, Secret);
        connection.SetCrypt(clientLocal, clientRemote);

        var payload = Sequence(28, i => i + 1);
        await connection.SendFeedbackAsync(TakionPacketType.FeedbackState, 7, 0, payload, timeout.Token);

        var packet = await console.ReceiveDatagramAsync(timeout.Token);
        Assert.Equal((byte)TakionPacketType.FeedbackState, packet[0]);
        Assert.Equal(7, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(1)));
        var keyPos = TakionPackets.ReadKeyPosLow(packet);
        Assert.True(TakionPackets.Verify(consoleVerifier, packet, keyPos));
        consoleVerifier.Transform(keyPos + 16, packet.AsSpan(12));
        Assert.Equal(payload, packet[12..]);
    }

    [Theory]
    [InlineData(0x10UL, 0x20u, 0x20UL)]
    [InlineData(0xFFFFFFF0UL, 0x10u, 0x1_0000_0010UL)]
    [InlineData(0x1_0000_0010UL, 0xFFFFFFF0u, 0xFFFFFFF0UL)]
    [InlineData(0x1_0000_0010UL, 0x20u, 0x1_0000_0020UL)]
    public void KeyPosTracker_HandlesWrapAround(ulong previous, uint low, ulong expected)
    {
        var tracker = new KeyPosTracker();
        tracker.Commit(previous);

        Assert.Equal(expected, tracker.Resolve(low));
    }

    private sealed class RecordingHandler : ITakionHandler
    {
        public System.Threading.Channels.Channel<(TakionDataType Type, byte[] Data)> Data { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<(TakionDataType, byte[])>();

        public System.Threading.Channels.Channel<(byte[] Datagram, ulong KeyPos)> Av { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<(byte[], ulong)>();

        public ConcurrentQueue<Exception?> Closed { get; } = new();

        public void OnData(TakionDataType type, ReadOnlySpan<byte> data) => Data.Writer.TryWrite((type, data.ToArray()));

        public void OnAvPacket(ReadOnlySpan<byte> datagram, ulong keyPos) => Av.Writer.TryWrite((datagram.ToArray(), keyPos));

        public void OnClosed(Exception? exception) => Closed.Enqueue(exception);
    }
}
