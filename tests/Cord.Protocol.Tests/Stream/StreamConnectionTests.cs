using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Cord.Protocol.Av;
using Cord.Protocol.Crypto;
using Cord.Protocol.Input;
using Cord.Protocol.Session;
using Cord.Protocol.Stream;
using Cord.Protocol.Stream.Messages;
using Cord.Protocol.Takion;
using Cord.Protocol.Tests.Av;
using Cord.Protocol.Tests.Support;
using Cord.Protocol.Tests.Takion;
using static Cord.Protocol.Stream.Messages.TakionMessage.Types;
using static Cord.Protocol.Tests.Support.TestBytes;

namespace Cord.Protocol.Tests.Stream;

public class StreamConnectionTests
{
    private static readonly StreamOptions Options = new(1920, 1080, 60, 15000, VideoCodec.Hevc);
    private static readonly byte[] Nonce = Sequence(16, i => 0xF0 - (i * 5));
    private static readonly byte[] Morning = Sequence(16, i => (i * 13) + 3);

    [Fact]
    public void LaunchSpec_ContainsRequestedQuality()
    {
        var json = LaunchSpec.BuildJson(Options, mtu: 1400, rttMs: 3, handshakeKey: Sequence(16, i => i));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var resolution = root.GetProperty("streamResolutions")[0];
        Assert.Equal(1920, resolution.GetProperty("resolution").GetProperty("width").GetInt32());
        Assert.Equal(1080, resolution.GetProperty("resolution").GetProperty("height").GetInt32());
        Assert.Equal(60, resolution.GetProperty("maxFps").GetInt32());
        Assert.Equal(15000, root.GetProperty("network").GetProperty("bwKbpsSent").GetInt32());
        Assert.Equal(1400, root.GetProperty("network").GetProperty("mtu").GetInt32());
        Assert.Equal("hevc", root.GetProperty("videoCodec").GetString());
        Assert.Equal("SDR", root.GetProperty("dynamicRange").GetString());
        Assert.Equal(Convert.ToBase64String(Sequence(16, i => i)), root.GetProperty("handshakeKey").GetString());
    }

    [Theory]
    [InlineData(0x19)]
    [InlineData(0x11)]
    public void ParsePadInfo_ReadsBothLayouts(int size)
    {
        var data = new byte[size];
        var offset = size == 0x19 ? 8 : 0;
        data[offset] = 2;
        data[offset + 1] = 10;
        data[offset + 2] = 20;
        data[offset + 3] = 30;

        var info = StreamConnection.ParsePadInfo(data);

        Assert.Equal(new PadInfo(2, 10, 20, 30, false, 0, 0), info);
    }

    [Fact]
    public void PacketStats_CombineWithSyntheticLoss_OverridesCapAndClamps()
    {
        var baseline = PacketStats.CombineWithSyntheticLoss(100, 10, maxLoss: 0.05, syntheticLossRatio: 0.0);
        Assert.Equal((105, 5), baseline);

        var throttled = PacketStats.CombineWithSyntheticLoss(100, 0, maxLoss: 0.05, syntheticLossRatio: 0.10);
        Assert.Equal((90, 10), throttled);

        var clamped = PacketStats.CombineWithSyntheticLoss(100, 0, maxLoss: 0.05, syntheticLossRatio: 0.80);
        Assert.Equal((70, 30), clamped);

        var empty = PacketStats.CombineWithSyntheticLoss(0, 0, maxLoss: 0.05, syntheticLossRatio: 0.20);
        Assert.Equal((0, 0), empty);
    }

    [Fact]
    public async Task Connect_NegotiatesKeysAndDeliversVideoAndInput()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var console = new FakeTakionConsole();
        using var clientCipher = RpCipher.ForSession(TestTables.Value, Nonce, Morning);
        using var consoleCipher = RpCipher.ForSession(TestTables.Value, Nonce, Morning);
        var secrets = SessionSecrets.Generate();
        var sink = new RecordingSink();
        var frame = Sequence(500, i => i * 7);
        var header = new byte[] { 0, 0, 0, 1, 0x40, 0x01 };

        var consoleTask = Task.Run(async () =>
        {
            await console.AcceptAsync(timeout.Token);

            var big = TakionMessage.Parser.ParseFrom(await ReceiveMessageAsync(console, timeout.Token));
            Assert.Equal(PayloadType.Big, big.Type);
            Assert.Equal(12u, big.BigPayload.ClientVersion);
            Assert.Equal("SessionId123", big.BigPayload.SessionKey);
            var launchSpec = DecryptLaunchSpec(big.BigPayload.LaunchSpec, consoleCipher);
            Assert.Contains("\"bwKbpsSent\":15000", launchSpec);

            using var keyExchange = new StreamKeyExchange();
            var secret = keyExchange.DeriveSecret(big.BigPayload.EcdhPubKey.Span, big.BigPayload.EcdhSig.Span, secrets.HandshakeKey);
            await console.SendDataAsync(TakionDataType.Protobuf, new TakionMessage
            {
                Type = PayloadType.Bang,
                BangPayload = new BangPayload
                {
                    ServerVersion = 12,
                    Token = 1,
                    EncryptedKeyAccepted = true,
                    VersionAccepted = true,
                    SessionKey = "",
                    EcdhPubKey = ByteString.CopyFrom(keyExchange.PublicKey),
                    EcdhSig = ByteString.CopyFrom(StreamKeyExchange.Sign(secrets.HandshakeKey, keyExchange.PublicKey)),
                },
            }.ToByteArray(), timeout.Token);

            using var consoleLocal = new GkCrypt(GkCrypt.RemoteIndex, secrets.HandshakeKey, secret);
            using var consoleVerifier = new GkCrypt(GkCrypt.LocalIndex, secrets.HandshakeKey, secret);
            console.LocalCrypt = consoleLocal;

            var audioHeader = new byte[] { 2, 16, 0, 0, 0xBB, 0x80, 0, 0, 0x01, 0xE0, 0, 0, 0, 1 };
            await console.SendDataAsync(TakionDataType.Protobuf, new TakionMessage
            {
                Type = PayloadType.Streaminfo,
                StreamInfoPayload = new StreamInfoPayload
                {
                    Resolution = { new ResolutionPayload { Width = 1920, Height = 1080, VideoHeader = ByteString.CopyFrom(header) } },
                    AudioHeader = ByteString.CopyFrom(audioHeader),
                },
            }.ToByteArray(), timeout.Token);

            var types = new List<PayloadType>();
            while (types.Count < 2)
                types.Add(TakionMessage.Parser.ParseFrom(await ReceiveMessageAsync(console, timeout.Token, consoleVerifier)).Type);
            Assert.Equal([PayloadType.Streaminfoack, PayloadType.Controllerconnection], types);

            await console.SendDataAsync(TakionDataType.TriggerEffects, Sequence(24, i => i), timeout.Token);
            var triggerEffects = Sequence(25, i => i);
            triggerEffects[1] = 0x21;
            triggerEffects[2] = 0x26;
            await console.SendDataAsync(TakionDataType.TriggerEffects, triggerEffects, timeout.Token);

            var units = AvTestPackets.EncodeFrame(frame, 3, 1);
            for (var u = 0; u < units.Length; u++)
            {
                var datagram = AvTestPackets.Video((ushort)u, 1, u, units.Length, 1, 0, units[u]);
                var keyPos = console.NextKeyPos(datagram.Length);
                consoleLocal.Transform(keyPos + 16, datagram.AsSpan(0x15));
                TakionPackets.Sign(consoleLocal, datagram, keyPos);
                await console.SendAsync(datagram, timeout.Token);
            }

            while (true)
            {
                var datagram = await console.ReceiveDatagramAsync(timeout.Token);
                if (TakionPackets.TypeOf(datagram) != TakionPacketType.FeedbackHistory)
                    continue;
                var keyPos = TakionPackets.ReadKeyPosLow(datagram);
                Assert.True(TakionPackets.Verify(consoleVerifier, datagram, keyPos));
                consoleVerifier.Transform(keyPos + 16, datagram.AsSpan(12));
                Assert.Equal(new byte[] { 0x80, 0x88, 0xFF }, datagram[12..15]);
                return;
            }
        });

        await using var stream = await StreamConnection.ConnectAsync(
            console.EndPoint, Options, clientCipher, secrets, "SessionId123", NetworkProbe.LanDefault, sink, timeout.Token);

        var received = await sink.Frames.Reader.ReadAsync(timeout.Token);
        Assert.Equal(frame, received.Frame);
        Assert.Equal(header, received.Header);
        Assert.Equal(48000, sink.Audio!.SampleRate);

        var effects = await sink.TriggerEffects.Reader.ReadAsync(timeout.Token);
        Assert.Equal(0x21, effects.Left.Type);
        Assert.Equal(Sequence(10, i => i + 5), effects.Left.Parameters);
        Assert.Equal(0x26, effects.Right.Type);
        Assert.Equal(Sequence(10, i => i + 15), effects.Right.Parameters);
        Assert.False(sink.TriggerEffects.Reader.TryRead(out _));

        stream.UpdateController(ControllerState.Idle with { Buttons = ControllerButtons.Cross });
        await consoleTask;
    }

    [Fact]
    public async Task Dispose_SendsControllerDisconnectAndDisconnectMessageWithAck()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var console = new FakeTakionConsole();
        using var clientCipher = RpCipher.ForSession(TestTables.Value, Nonce, Morning);
        using var consoleCipher = RpCipher.ForSession(TestTables.Value, Nonce, Morning);
        var secrets = SessionSecrets.Generate();
        var sink = new RecordingSink();
        var teardownMessages = new ConcurrentQueue<TakionMessage>();

        var consoleTask = Task.Run(async () =>
        {
            await console.AcceptAsync(timeout.Token);
            var big = TakionMessage.Parser.ParseFrom(await ReceiveMessageAsync(console, timeout.Token));

            using var keyExchange = new StreamKeyExchange();
            var secret = keyExchange.DeriveSecret(big.BigPayload.EcdhPubKey.Span, big.BigPayload.EcdhSig.Span, secrets.HandshakeKey);
            await console.SendDataAsync(TakionDataType.Protobuf, new TakionMessage
            {
                Type = PayloadType.Bang,
                BangPayload = new BangPayload
                {
                    ServerVersion = 12,
                    Token = 1,
                    EncryptedKeyAccepted = true,
                    VersionAccepted = true,
                    SessionKey = "",
                    EcdhPubKey = ByteString.CopyFrom(keyExchange.PublicKey),
                    EcdhSig = ByteString.CopyFrom(StreamKeyExchange.Sign(secrets.HandshakeKey, keyExchange.PublicKey)),
                },
            }.ToByteArray(), timeout.Token);

            using var consoleLocal = new GkCrypt(GkCrypt.RemoteIndex, secrets.HandshakeKey, secret);
            using var consoleVerifier = new GkCrypt(GkCrypt.LocalIndex, secrets.HandshakeKey, secret);
            console.LocalCrypt = consoleLocal;

            var header = new byte[] { 0, 0, 0, 1, 0x40, 0x01 };
            var audioHeader = new byte[] { 2, 16, 0, 0, 0xBB, 0x80, 0, 0, 0x01, 0xE0, 0, 0, 0, 1 };
            await console.SendDataAsync(TakionDataType.Protobuf, new TakionMessage
            {
                Type = PayloadType.Streaminfo,
                StreamInfoPayload = new StreamInfoPayload
                {
                    Resolution = { new ResolutionPayload { Width = 1920, Height = 1080, VideoHeader = ByteString.CopyFrom(header) } },
                    AudioHeader = ByteString.CopyFrom(audioHeader),
                },
            }.ToByteArray(), timeout.Token);

            while (!timeout.Token.IsCancellationRequested)
            {
                var msg = TakionMessage.Parser.ParseFrom(await ReceiveMessageAsync(console, timeout.Token, consoleVerifier));
                teardownMessages.Enqueue(msg);
                if (msg.Type == PayloadType.Disconnect)
                    return;
            }
        });

        var stream = await StreamConnection.ConnectAsync(
            console.EndPoint, Options, clientCipher, secrets, "SessionId123", NetworkProbe.LanDefault, sink, timeout.Token);

        // Ensure StreamInfoAck & initial ControllerConnection have arrived before disconnecting
        while (!teardownMessages.Any(m => m.Type == PayloadType.Controllerconnection && m.ControllerConnectionPayload.Connected))
            await Task.Delay(15, timeout.Token);

        await stream.DisposeAsync();
        await consoleTask;

        Assert.Contains(teardownMessages, m => m.Type == PayloadType.Controllerconnection && !m.ControllerConnectionPayload.Connected);
        Assert.Contains(teardownMessages, m => m.Type == PayloadType.Disconnect && m.DisconnectPayload?.Reason == "Client Disconnecting");
    }

    private static string DecryptLaunchSpec(string base64, RpCipher cipher)
    {
        var data = Convert.FromBase64String(base64);
        var mask = cipher.Encrypt(0, new byte[data.Length]);
        for (var i = 0; i < data.Length; i++)
            data[i] ^= mask[i];
        return Encoding.UTF8.GetString(data).TrimEnd('\0');
    }

    /// <summary>Reassembles a possibly fragmented message and acknowledges each chunk.</summary>
    private static async Task<byte[]> ReceiveMessageAsync(FakeTakionConsole console, CancellationToken cancellationToken, GkCrypt? verifier = null)
    {
        var message = new List<byte>();
        while (true)
        {
            var datagram = await console.ReceiveDatagramAsync(cancellationToken);
            if (!TakionPackets.TryParseControl(datagram, out var chunk) || chunk.Type != TakionChunkType.Data)
                continue;
            if (verifier is not null)
                Assert.True(TakionPackets.Verify(verifier, datagram, chunk.KeyPosLow));

            var sequence = BinaryPrimitives.ReadUInt32BigEndian(chunk.Payload);
            var flags = chunk.Flags;
            message.AddRange(chunk.Payload[(message.Count == 0 ? 9 : 8)..].ToArray());
            await console.AckAsync(sequence, cancellationToken);
            if (flags == 1)
                return [.. message];
        }
    }

    private sealed class RecordingSink : IStreamSink
    {
        public System.Threading.Channels.Channel<(byte[] Frame, byte[]? Header)> Frames { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<(byte[], byte[]?)>();

        public AudioFormat? Audio { get; private set; }

        public System.Threading.Channels.Channel<(TriggerEffect Left, TriggerEffect Right)> TriggerEffects { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<(TriggerEffect, TriggerEffect)>();

        public ConcurrentQueue<string> Disconnects { get; } = new();

        public void OnStreamInfo(IReadOnlyList<VideoStreamProfile> profiles, AudioFormat audio) => Audio = audio;

        public void OnVideoFrame(ReadOnlyMemory<byte> frame, ReadOnlyMemory<byte>? streamHeader, bool recoveredWithFec) =>
            Frames.Writer.TryWrite((frame.ToArray(), streamHeader?.ToArray()));

        public void OnAudioFrame(ReadOnlySpan<byte> opusFrame, bool haptics)
        {
        }

        public void OnRumble(byte left, byte right)
        {
        }

        public void OnTriggerEffects(TriggerEffect left, TriggerEffect right) => TriggerEffects.Writer.TryWrite((left, right));

        public void OnPadInfo(PadInfo info)
        {
        }

        public void OnDisconnected(string reason) => Disconnects.Enqueue(reason);
    }
}
