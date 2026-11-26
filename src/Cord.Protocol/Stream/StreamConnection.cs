using System.Net;
using Google.Protobuf;
using Cord.Protocol.Av;
using Cord.Protocol.Crypto;
using Cord.Protocol.Input;
using Cord.Protocol.Session;
using Cord.Protocol.Stream.Messages;
using Cord.Protocol.Takion;
using static Cord.Protocol.Stream.Messages.TakionMessage.Types;

namespace Cord.Protocol.Stream;

/// <summary>
/// The streaming Takion connection: key exchange, stream setup, AV delivery, input and
/// periodic reports (docs/protocol/09-stream-connection.md).
/// </summary>
public sealed class StreamConnection : ITakionHandler, IVideoReceiverSink, IAudioReceiverSink, IFeedbackTransport, IAsyncDisposable
{
    public const int DefaultPort = 9296;
    public const uint ClientVersion = 12;

    /// <summary>Start of the <see cref="Disconnected"/> reason when the console stopped sending.</summary>
    public const string ConnectionLostReason = "Connection lost.";

    private const int CorruptKeyframeCooldownMs = 350;
    private static readonly TimeSpan ExpectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PeriodicKeyFrameInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CongestionInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DisconnectAckTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>A streaming console sends video, audio and acks continuously; this much silence means the link is gone.</summary>
    private static readonly TimeSpan ConnectionLostTimeout = TimeSpan.FromSeconds(5);

    private readonly IStreamSink _sink;
    private readonly StreamOptions _options;
    private readonly SessionSecrets _secrets;
    private readonly StreamKeyExchange _keyExchange = new();
    private readonly TaskCompletionSource<BangPayload> _bang = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<StreamInfoPayload> _streamInfo = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PacketStats _videoStats = new();
    private readonly CancellationTokenSource _stop = new();
    private TakionConnection? _takion;
    private GkCrypt? _localCrypt;
    private GkCrypt? _remoteCrypt;
    private VideoReceiver? _video;
    private AudioReceiver? _audio;
    private FeedbackSender? _feedback;
    private Task? _periodic;
    private long _lastCorruptIdrTicks;
    private double _externalCongestionLoss;

    private StreamConnection(IStreamSink sink, StreamOptions options, SessionSecrets secrets)
    {
        _sink = sink;
        _options = options;
        _secrets = secrets;
    }

    /// <summary>Completes with the reason when the console or the network ends the stream.</summary>
    public Task<string> Disconnected => _disconnected.Task;

    public VideoStreamProfile? CurrentVideoProfile => _video?.CurrentProfile;

    public double LossPercent => _videoStats.LastLossPercent;

    public double JitterMs => _videoStats.JitterMs;

    public int FramesLost => _video?.FramesLost ?? 0;

    public double ExternalCongestionLoss => Volatile.Read(ref _externalCongestionLoss);

    public void SetExternalCongestionLoss(double lossRatio) =>
        Volatile.Write(ref _externalCongestionLoss, Math.Clamp(lossRatio, 0.0, 0.30));

    public static async Task<StreamConnection> ConnectAsync(
        IPEndPoint console,
        StreamOptions options,
        RpCipher sessionCipher,
        SessionSecrets secrets,
        string sessionId,
        NetworkProbe network,
        IStreamSink sink,
        CancellationToken cancellationToken = default)
    {
        var connection = new StreamConnection(sink, options, secrets);
        try
        {
            await connection.StartAsync(console, sessionCipher, sessionId, network, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public void UpdateController(in ControllerState state) => _feedback?.Update(state);

    public Task RequestKeyFrameAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new TakionMessage { Type = PayloadType.Idrrequest }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_feedback is not null)
        {
            await _feedback.DisposeAsync();
            _feedback = null;
        }

        if (_takion is not null && !_disconnected.Task.IsCompleted)
        {
            try
            {
                await SendAsync(new TakionMessage
                {
                    Type = PayloadType.Controllerconnection,
                    ControllerConnectionPayload = new ControllerConnectionPayload
                    {
                        Connected = false,
                        ControllerType = _options.DualSense
                            ? ControllerConnectionPayload.Types.ControllerType.Dualsense
                            : ControllerConnectionPayload.Types.ControllerType.Dualshock4,
                    },
                }, CancellationToken.None);
                await SendAsync(new TakionMessage
                {
                    Type = PayloadType.Disconnect,
                    DisconnectPayload = new DisconnectPayload { Reason = "Client Disconnecting" },
                }, CancellationToken.None);
                await _takion.FlushUnacknowledgedAsync(DisconnectAckTimeout);
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or ObjectDisposedException)
            {
            }
        }

        await _stop.CancelAsync();
        if (_periodic is not null)
        {
            try
            {
                await _periodic;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_takion is not null)
            await _takion.DisposeAsync();
        _localCrypt?.Dispose();
        _remoteCrypt?.Dispose();
        _keyExchange.Dispose();
        _stop.Dispose();
    }

    void ITakionHandler.OnData(TakionDataType type, ReadOnlySpan<byte> data)
    {
        switch (type)
        {
            case TakionDataType.Protobuf:
                HandleMessage(data);
                break;
            case TakionDataType.Rumble when data.Length >= 3:
                _sink.OnRumble(data[1], data[2]);
                break;
            case TakionDataType.TriggerEffects when data.Length >= 25:
                _sink.OnTriggerEffects(new TriggerEffect(data[1], data.Slice(5, 10).ToArray()), new TriggerEffect(data[2], data.Slice(15, 10).ToArray()));
                break;
            case TakionDataType.PadInfo:
                if (ParsePadInfo(data) is { } info)
                    _sink.OnPadInfo(info);
                break;
        }
    }

    void ITakionHandler.OnAvPacket(ReadOnlySpan<byte> datagram, ulong keyPos)
    {
        if (_remoteCrypt is null || !AvPacket.TryParse(datagram, out var packet))
            return;

        if (_options.AudioOnly && packet.IsVideo)
        {
            _videoStats.Record(packet.PacketIndex);
            return;
        }

        var payload = datagram[packet.PayloadOffset..].ToArray();
        _remoteCrypt.Transform(keyPos + GkCrypt.BlockSize, payload);

        if (packet.IsVideo)
        {
            _videoStats.Record(packet.PacketIndex);
            _video?.OnPacket(packet, payload);
        }
        else
        {
            _audio?.OnPacket(packet, payload);
        }
    }

    void ITakionHandler.OnClosed(Exception? exception) => _disconnected.TrySetResult(exception switch
    {
        null => "Connection closed",
        TimeoutException => $"{ConnectionLostReason} {exception.Message}",
        _ => exception.Message,
    });

    void IVideoReceiverSink.OnFrame(ReadOnlyMemory<byte> frame, ReadOnlyMemory<byte>? streamHeader, bool recoveredWithFec) =>
        _sink.OnVideoFrame(frame, streamHeader, recoveredWithFec);

    void IVideoReceiverSink.OnCorruptFrames(ushort first, ushort last)
    {
        _ = SendAsync(new TakionMessage
        {
            Type = PayloadType.Corruptframe,
            CorruptPayload = new CorruptFramePayload { Start = first, End = last },
        }, _stop.Token);

        var now = Environment.TickCount64;
        var prev = Volatile.Read(ref _lastCorruptIdrTicks);
        if (now - prev >= CorruptKeyframeCooldownMs
            && Interlocked.CompareExchange(ref _lastCorruptIdrTicks, now, prev) == prev)
        {
            _ = RequestKeyFrameAsync(_stop.Token);
        }
    }

    void IAudioReceiverSink.OnAudioFrame(ushort frameIndex, ReadOnlySpan<byte> opusFrame, bool haptics) =>
        _sink.OnAudioFrame(opusFrame, haptics);

    Task IFeedbackTransport.SendFeedbackAsync(TakionPacketType type, ushort sequenceNumber, byte countMinusOne, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        _takion!.SendFeedbackAsync(type, sequenceNumber, countMinusOne, payload, cancellationToken);

    internal static PadInfo? ParsePadInfo(ReadOnlySpan<byte> data) => data.Length switch
    {
        0x19 => new PadInfo(data[8], data[9], data[10], data[11], data[12] != 0, data[20], data[21]),
        0x11 => new PadInfo(data[0], data[1], data[2], data[3], data[4] != 0, data[12], data[13]),
        _ => null,
    };

    private async Task StartAsync(IPEndPoint console, RpCipher sessionCipher, string sessionId, NetworkProbe network, CancellationToken cancellationToken)
    {
        _takion = await TakionConnection.ConnectAsync(console, this, dontFragment: false, cancellationToken);

        var big = new TakionMessage
        {
            Type = PayloadType.Big,
            BigPayload = new BigPayload
            {
                ClientVersion = ClientVersion,
                SessionKey = sessionId,
                LaunchSpec = LaunchSpec.Encrypt(LaunchSpec.BuildJson(_options, network.MtuIn, network.RttMilliseconds, _secrets.HandshakeKey), sessionCipher),
                EncryptedKey = ByteString.CopyFrom(new byte[4]),
                EcdhPubKey = ByteString.CopyFrom(_keyExchange.PublicKey),
                EcdhSig = ByteString.CopyFrom(StreamKeyExchange.Sign(_secrets.HandshakeKey, _keyExchange.PublicKey)),
            },
        };
        var mtu = Math.Min(network.MtuIn, network.MtuOut) - 50;
        await _takion.SendMessageAsync(TakionDataType.Protobuf, big.ToByteArray(), mtu - 26, cancellationToken);

        var bang = await WaitAsync(_bang.Task, "BANG", cancellationToken);
        if (!bang.VersionAccepted || !bang.EncryptedKeyAccepted || !bang.HasEcdhPubKey || !bang.HasEcdhSig)
            throw new RemotePlayException(RemotePlayError.InvalidResponse, "The console rejected the stream request.");

        var secret = _keyExchange.DeriveSecret(bang.EcdhPubKey.Span, bang.EcdhSig.Span, _secrets.HandshakeKey);
        _localCrypt = new GkCrypt(GkCrypt.LocalIndex, _secrets.HandshakeKey, secret);
        _remoteCrypt = new GkCrypt(GkCrypt.RemoteIndex, _secrets.HandshakeKey, secret);
        _takion.SetCrypt(_localCrypt, _remoteCrypt);

        var info = await WaitAsync(_streamInfo.Task, "STREAMINFO", cancellationToken);
        var profiles = info.Resolution.Select(r => new VideoStreamProfile((int)r.Width, (int)r.Height, r.VideoHeader.ToByteArray())).ToList();
        var audioFormat = AudioFormat.Parse(info.AudioHeader.Span);
        _sink.OnStreamInfo(profiles, audioFormat);
        if (!_options.AudioOnly)
            _video = new VideoReceiver(profiles, this, dropCorruptUntilKeyframe: true, isHevc: _options.Codec != VideoCodec.H264);
        _audio = new AudioReceiver(this);

        await SendAsync(new TakionMessage { Type = PayloadType.Streaminfoack }, cancellationToken);
        await SendAsync(new TakionMessage
        {
            Type = PayloadType.Controllerconnection,
            ControllerConnectionPayload = new ControllerConnectionPayload
            {
                Connected = true,
                ControllerType = _options.DualSense
                    ? ControllerConnectionPayload.Types.ControllerType.Dualsense
                    : ControllerConnectionPayload.Types.ControllerType.Dualshock4,
            },
        }, cancellationToken);

        _feedback = new FeedbackSender(this, stateMinInterval: _options.AudioOnly ? TimeSpan.FromMilliseconds(2) : null);
        _periodic = Task.Run(() => RunPeriodicAsync(_stop.Token), CancellationToken.None);
        _takion.SilenceTimeout = ConnectionLostTimeout;
    }

    private async Task<T> WaitAsync<T>(Task<T> task, string name, CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(task, _disconnected.Task, Task.Delay(ExpectTimeout, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (completed == task)
            return await task;
        if (completed == _disconnected.Task)
            throw new RemotePlayException(RemotePlayError.Disconnected, $"The console disconnected: {await _disconnected.Task}");
        throw new RemotePlayException(RemotePlayError.InvalidResponse, $"Timed out waiting for {name}.");
    }

    private async Task RunPeriodicAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(CongestionInterval);
        var heartbeatDue = DateTime.UtcNow + HeartbeatInterval;
        var keyframeDue = DateTime.UtcNow + PeriodicKeyFrameInterval;
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var (received, lost) = _videoStats.TakeSnapshot();
            var report = PacketStats.CombineWithSyntheticLoss(
                received,
                lost,
                _options.MaxReportedPacketLoss,
                Volatile.Read(ref _externalCongestionLoss));
            await _takion!.SendCongestionAsync(report.Received, report.Lost, cancellationToken);

            var now = DateTime.UtcNow;
            if (now >= heartbeatDue)
            {
                heartbeatDue = now + HeartbeatInterval;
                await SendAsync(new TakionMessage { Type = PayloadType.Heartbeat }, cancellationToken);
            }

            if (!_options.AudioOnly && now >= keyframeDue)
            {
                keyframeDue = now + PeriodicKeyFrameInterval;
                await RequestKeyFrameAsync(cancellationToken);
            }
        }
    }

    private Task SendAsync(TakionMessage message, CancellationToken cancellationToken) =>
        _takion!.SendMessageAsync(TakionDataType.Protobuf, message.ToByteArray(), cancellationToken: cancellationToken);

    private void HandleMessage(ReadOnlySpan<byte> data)
    {
        TakionMessage message;
        try
        {
            message = TakionMessage.Parser.ParseFrom(data);
        }
        catch (InvalidProtocolBufferException)
        {
            return;
        }

        switch (message.Type)
        {
            case PayloadType.Bang when message.BangPayload is not null:
                _bang.TrySetResult(message.BangPayload);
                break;
            case PayloadType.Streaminfo when message.StreamInfoPayload is not null:
                _streamInfo.TrySetResult(message.StreamInfoPayload);
                break;
            case PayloadType.Disconnect:
                var reason = message.DisconnectPayload?.Reason ?? "Disconnected";
                _disconnected.TrySetResult(reason);
                _sink.OnDisconnected(reason);
                break;
        }
    }
}
