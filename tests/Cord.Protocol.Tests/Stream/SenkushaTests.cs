using System.Buffers.Binary;
using Google.Protobuf;
using Cord.Protocol.Stream;
using Cord.Protocol.Stream.Messages;
using Cord.Protocol.Takion;
using Cord.Protocol.Tests.Takion;
using static Cord.Protocol.Stream.Messages.TakionMessage.Types;

namespace Cord.Protocol.Tests.Stream;

public class SenkushaTests
{
    [Fact]
    public void BuildPing_UsesV7AudioLayout()
    {
        var ping = Senkusha.BuildPing(testIndex: 0, pingIndex: 3, tag: 0xCAFEBABE);

        Assert.Equal(0x224, ping.Length);
        Assert.Equal((byte)TakionPacketType.Audio, ping[0]);
        Assert.Equal(0xFF, ping[9]);
        var units = BinaryPrimitives.ReadUInt32BigEndian(ping.AsSpan(5));
        Assert.Equal(3u, units >> 21);
        Assert.Equal(0x7FFu, (units >> 10) & 0x7FF);
        Assert.Equal(0xCAFEBABEu, BinaryPrimitives.ReadUInt32BigEndian(ping.AsSpan(0x16)));
    }

    [Fact]
    public async Task RunAsync_MeasuresRttAgainstEchoingConsole()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var console = new FakeTakionConsole();

        var consoleTask = Task.Run(async () =>
        {
            await console.AcceptAsync(timeout.Token);
            while (true)
            {
                var datagram = await console.ReceiveDatagramAsync(timeout.Token);
                if (TakionPackets.TypeOf(datagram) == TakionPacketType.Audio)
                {
                    await console.SendAsync(datagram, timeout.Token);
                    continue;
                }

                if (!TakionPackets.TryParseControl(datagram, out var chunk) || chunk.Type != TakionChunkType.Data)
                    continue;
                var sequence = BinaryPrimitives.ReadUInt32BigEndian(chunk.Payload);
                var message = TakionMessage.Parser.ParseFrom(chunk.Payload[9..].ToArray());
                await console.AckAsync(sequence, timeout.Token);

                switch (message.Type)
                {
                    case PayloadType.Takionprotocolrequest:
                        Assert.Equal([9u], message.TakionProtocolRequest.SupportedTakionVersions);
                        await Reply(new TakionMessage { Type = PayloadType.Takionprotocolrequestack, TakionProtocolRequestAck = new() { TakionProtocolVersion = 9 } });
                        break;
                    case PayloadType.Big:
                        await Reply(new TakionMessage
                        {
                            Type = PayloadType.Bang,
                            BangPayload = new BangPayload { ServerVersion = 9, Token = 0, EncryptedKeyAccepted = true, VersionAccepted = true, SessionKey = "" },
                        });
                        break;
                    case PayloadType.Disconnect:
                        return;
                }
            }

            Task Reply(TakionMessage reply) => console.SendDataAsync(TakionDataType.Protobuf, reply.ToByteArray(), timeout.Token);
        });

        var probe = await Senkusha.RunAsync(console.EndPoint, timeout.Token);

        Assert.InRange(probe.RttMilliseconds, 1, 100);
        Assert.Equal(NetworkProbe.LanDefault.MtuIn, probe.MtuIn);
        await consoleTask;
    }
}
