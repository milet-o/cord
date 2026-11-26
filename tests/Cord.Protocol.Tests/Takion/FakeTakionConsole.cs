using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Cord.Protocol.Crypto;
using Cord.Protocol.Takion;

namespace Cord.Protocol.Tests.Takion;

/// <summary>Minimal console side of Takion for tests.</summary>
internal sealed class FakeTakionConsole : IDisposable
{
    public const uint ConsoleTag = 0x11223344;

    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private IPEndPoint? _client;
    private uint _clientTag;
    private uint _nextSequence = ConsoleTag;
    private ulong _keyPos;

    public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    public GkCrypt? LocalCrypt { get; set; }

    public async Task AcceptAsync(CancellationToken cancellationToken)
    {
        var init = await ReceiveControlAsync(TakionChunkType.Init, cancellationToken);
        _clientTag = TakionPackets.ReadInit(init).Tag;
        var cookie = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        await SendAsync(TakionPackets.BuildControl(_clientTag, TakionChunkType.InitAck, 0,
            TakionPackets.BuildInitAck(new InitParameters(ConsoleTag, 0x19000, 0x64, 0x64, ConsoleTag), cookie)), cancellationToken);

        var echoed = await ReceiveControlAsync(TakionChunkType.Cookie, cancellationToken);
        Assert.Equal(cookie, echoed);
        await SendAsync(TakionPackets.BuildControl(_clientTag, TakionChunkType.CookieAck, 0, []), cancellationToken);
    }

    public async Task<byte[]> ReceiveDatagramAsync(CancellationToken cancellationToken)
    {
        var result = await _udp.ReceiveAsync(cancellationToken);
        _client = result.RemoteEndPoint;
        return result.Buffer;
    }

    public async Task<byte[]> ReceiveControlAsync(TakionChunkType type, CancellationToken cancellationToken)
    {
        while (true)
        {
            var datagram = await ReceiveDatagramAsync(cancellationToken);
            if (TakionPackets.TryParseControl(datagram, out var chunk) && chunk.Type == type)
                return chunk.Payload.ToArray();
        }
    }

    public Task SendDataAsync(TakionDataType type, byte[] data, CancellationToken cancellationToken) =>
        SendDataAsync(_nextSequence++, type, data, cancellationToken);

    public async Task SendDataAsync(uint sequence, TakionDataType type, byte[] data, CancellationToken cancellationToken)
    {
        var packet = TakionPackets.BuildControl(_clientTag, TakionChunkType.Data, 1, TakionPackets.BuildData(sequence, 1, type, data));
        if (LocalCrypt is not null)
            TakionPackets.Sign(LocalCrypt, packet, NextKeyPos(data.Length));
        await SendAsync(packet, cancellationToken);
    }

    public async Task AckAsync(uint sequence, CancellationToken cancellationToken) =>
        await SendAsync(TakionPackets.BuildControl(_clientTag, TakionChunkType.DataAck, 0, TakionPackets.BuildDataAck(sequence, 0x19000)), cancellationToken);

    public async Task SendAsync(byte[] datagram, CancellationToken cancellationToken) =>
        await _udp.SendAsync(datagram, _client!, cancellationToken);

    public ulong NextKeyPos(int size)
    {
        var current = _keyPos;
        _keyPos += (ulong)(size + (size % 16));
        return current;
    }

    public static uint SequenceOf(ReadOnlySpan<byte> dataPayload) => BinaryPrimitives.ReadUInt32BigEndian(dataPayload);

    public void Dispose() => _udp.Dispose();
}
