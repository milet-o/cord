using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Google.Protobuf;
using Cord.Protocol.Stream.Messages;
using Cord.Protocol.Takion;
using static Cord.Protocol.Stream.Messages.TakionMessage.Types;

namespace Cord.Protocol.Stream;

/// <summary>
/// Network probe run before streaming (docs/protocol/10-senkusha.md). Measures RTT; the MTU
/// tests are not implemented yet, so the LAN default MTU is reported.
/// </summary>
public sealed class Senkusha : ITakionHandler
{
    public const int DefaultPort = 9297;
    public const uint ProbeTakionVersion = 9;

    private const int PingCount = 10;
    private const int PingDatagramSize = 0x224;
    private const int V7HeaderSize = 0x12;
    private const ushort PingUnitsTotal = 0x800;
    private const byte PingCodec = 0xFF;
    private static readonly TimeSpan ExpectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(1);

    private readonly TaskCompletionSource _protocolAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _bang = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<long>? _pong;
    private uint _pingTag;

    private Senkusha()
    {
    }

    public static async Task<NetworkProbe> RunAsync(IPEndPoint console, CancellationToken cancellationToken = default)
    {
        var probe = new Senkusha();
        await using var takion = await TakionConnection.ConnectAsync(console, probe, dontFragment: true, encrypted: false, cancellationToken);

        await takion.SendMessageAsync(TakionDataType.Protobuf, new TakionMessage
        {
            Type = PayloadType.Takionprotocolrequest,
            TakionProtocolRequest = new TakionProtocolRequestPayload { SupportedTakionVersions = { ProbeTakionVersion } },
        }.ToByteArray(), cancellationToken: cancellationToken);
        await WaitAsync(probe._protocolAck.Task, cancellationToken);

        await takion.SendMessageAsync(TakionDataType.Protobuf, new TakionMessage
        {
            Type = PayloadType.Big,
            BigPayload = new BigPayload { ClientVersion = ProbeTakionVersion, SessionKey = "", LaunchSpec = "", EncryptedKey = ByteString.Empty },
        }.ToByteArray(), cancellationToken: cancellationToken);
        await WaitAsync(probe._bang.Task, cancellationToken);

        var rtt = await probe.MeasureRttAsync(takion, cancellationToken);

        await takion.SendMessageAsync(TakionDataType.Protobuf, new TakionMessage
        {
            Type = PayloadType.Disconnect,
            DisconnectPayload = new DisconnectPayload { Reason = "Client Disconnecting" },
        }.ToByteArray(), cancellationToken: cancellationToken);

        var rttMilliseconds = Math.Max(1, (int)Math.Round(rtt.TotalMilliseconds));
        return NetworkProbe.LanDefault with { RttMilliseconds = rttMilliseconds };
    }

    void ITakionHandler.OnData(TakionDataType type, ReadOnlySpan<byte> data)
    {
        if (type != TakionDataType.Protobuf)
            return;
        TakionMessage message;
        try
        {
            message = TakionMessage.Parser.ParseFrom(data);
        }
        catch (InvalidProtocolBufferException)
        {
            return;
        }

        if (message.Type == PayloadType.Takionprotocolrequestack)
            _protocolAck.TrySetResult();
        else if (message.Type == PayloadType.Bang)
            _bang.TrySetResult();
    }

    void ITakionHandler.OnAvPacket(ReadOnlySpan<byte> datagram, ulong keyPos)
    {
        if (datagram.Length >= V7HeaderSize + 8
            && TakionPackets.TypeOf(datagram) == TakionPacketType.Audio
            && BinaryPrimitives.ReadUInt32BigEndian(datagram[(V7HeaderSize + 4)..]) == _pingTag)
        {
            _pong?.TrySetResult(Stopwatch.GetTimestamp());
        }
    }

    void ITakionHandler.OnClosed(Exception? exception)
    {
    }

    internal static byte[] BuildPing(ushort testIndex, ushort pingIndex, uint tag)
    {
        var datagram = new byte[PingDatagramSize];
        datagram[0] = (byte)TakionPacketType.Audio;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(3), testIndex);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(5), (uint)(((PingUnitsTotal - 1) & 0x7FF) << 10) | ((uint)pingIndex << 21));
        datagram[9] = PingCodec;
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(V7HeaderSize + 4), tag);
        return datagram;
    }

    private async Task<TimeSpan> MeasureRttAsync(TakionConnection takion, CancellationToken cancellationToken)
    {
        await SendEchoAsync(takion, true, cancellationToken);

        var total = TimeSpan.Zero;
        var successful = 0;
        for (ushort ping = 0; ping < PingCount; ping++)
        {
            _pingTag = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
            _pong = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sent = Stopwatch.GetTimestamp();
            await takion.SendRawAsync(BuildPing(0, ping, _pingTag), cancellationToken);

            var completed = await Task.WhenAny(_pong.Task, Task.Delay(PongTimeout, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (completed != _pong.Task)
                continue;
            total += Stopwatch.GetElapsedTime(sent, await _pong.Task);
            successful++;
        }

        await SendEchoAsync(takion, false, cancellationToken);

        if (successful == 0)
            throw new RemotePlayException(RemotePlayError.InvalidResponse, "The console answered no senkusha ping.");
        return total / successful;
    }

    private static Task SendEchoAsync(TakionConnection takion, bool enable, CancellationToken cancellationToken) =>
        takion.SendMessageAsync(TakionDataType.Protobuf, new TakionMessage
        {
            Type = PayloadType.Senkusha,
            SenkushaPayload = new SenkushaPayload
            {
                Command = SenkushaPayload.Types.Command.EchoCommand,
                EchoCommand = new SenkushaEchoCommand { State = enable },
            },
        }.ToByteArray(), cancellationToken: cancellationToken);

    private static async Task WaitAsync(Task task, CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(task, Task.Delay(ExpectTimeout, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (completed != task)
            throw new RemotePlayException(RemotePlayError.InvalidResponse, "Senkusha timed out.");
    }
}
