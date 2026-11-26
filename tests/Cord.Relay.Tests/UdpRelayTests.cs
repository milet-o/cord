using System.Net;
using System.Net.Sockets;

namespace Cord.Relay.Tests;

public sealed class UdpRelayTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Silence = TimeSpan.FromMilliseconds(300);

    private readonly List<Socket> _sockets = [];

    public void Dispose()
    {
        foreach (var socket in _sockets)
            socket.Dispose();
    }

    [Fact]
    public async Task ForwardsDatagramsToTheConsoleAndRepliesBackToTheClient()
    {
        var console = Udp();
        using var relay = Relay(console);
        var client = Udp();

        await client.SendToAsync("SRCH"u8.ToArray(), relay.LocalEndPoint);
        var (request, upstream) = await ReceiveAsync(console);
        await console.SendToAsync("HTTP/1.1 200 Ok"u8.ToArray(), upstream);
        var (reply, sender) = await ReceiveAsync(client);

        Assert.Equal("SRCH"u8.ToArray(), request);
        Assert.Equal("HTTP/1.1 200 Ok"u8.ToArray(), reply);
        Assert.Equal(relay.LocalEndPoint.Port, sender.Port);
        Assert.Equal([IPAddress.Loopback], relay.Clients);
    }

    [Fact]
    public async Task EachClientGetsItsOwnUpstreamSocket()
    {
        var console = Udp();
        using var relay = Relay(console);
        var first = Udp();
        var second = Udp();

        await first.SendToAsync("first"u8.ToArray(), relay.LocalEndPoint);
        var (_, firstUpstream) = await ReceiveAsync(console);
        await second.SendToAsync("second"u8.ToArray(), relay.LocalEndPoint);
        var (_, secondUpstream) = await ReceiveAsync(console);
        await console.SendToAsync("to second"u8.ToArray(), secondUpstream);
        await console.SendToAsync("to first"u8.ToArray(), firstUpstream);

        Assert.NotEqual(firstUpstream, secondUpstream);
        Assert.Equal("to first"u8.ToArray(), (await ReceiveAsync(first)).Data);
        Assert.Equal("to second"u8.ToArray(), (await ReceiveAsync(second)).Data);
    }

    [Fact]
    public async Task AlsoSendsToTheBroadcastAddressWhenGiven()
    {
        var console = Udp();
        var broadcast = Udp();
        using var relay = new UdpRelay(Loopback(), (IPEndPoint)console.LocalEndPoint!, (IPEndPoint)broadcast.LocalEndPoint!, _ => true);
        var client = Udp();

        await client.SendToAsync("SRC3\0"u8.ToArray(), relay.LocalEndPoint);

        Assert.Equal("SRC3\0"u8.ToArray(), (await ReceiveAsync(console)).Data);
        Assert.Equal("SRC3\0"u8.ToArray(), (await ReceiveAsync(broadcast)).Data);
    }

    [Fact]
    public async Task OnlyForwardsRepliesThatComeFromTheConsole()
    {
        var console = Udp();
        using var relay = Relay(console);
        var client = Udp();
        var stranger = Udp();

        await client.SendToAsync("hello"u8.ToArray(), relay.LocalEndPoint);
        var (_, upstream) = await ReceiveAsync(console);
        await stranger.SendToAsync("spoofed"u8.ToArray(), upstream);
        await console.SendToAsync("genuine"u8.ToArray(), upstream);

        Assert.Equal("genuine"u8.ToArray(), (await ReceiveAsync(client)).Data);
    }

    [Fact]
    public async Task IgnoresClientsTheFilterRejects()
    {
        var console = Udp();
        using var relay = new UdpRelay(Loopback(), (IPEndPoint)console.LocalEndPoint!, null, _ => false);
        var client = Udp();

        await client.SendToAsync("SRCH"u8.ToArray(), relay.LocalEndPoint);

        Assert.False(await ArrivesAsync(console));
        Assert.Empty(relay.Clients);
    }

    [Fact]
    public async Task ForgetsClientsThatWentQuiet()
    {
        var time = new ManualTime();
        var console = Udp();
        using var relay = new UdpRelay(Loopback(), (IPEndPoint)console.LocalEndPoint!, null, _ => true, time);
        var client = Udp();
        await client.SendToAsync("hello"u8.ToArray(), relay.LocalEndPoint);
        await ReceiveAsync(console);

        time.Advance(TimeSpan.FromSeconds(29));
        relay.RemoveIdle();
        Assert.NotEmpty(relay.Clients);

        time.Advance(TimeSpan.FromSeconds(2));
        relay.RemoveIdle();
        Assert.Empty(relay.Clients);
    }

    private static UdpRelay Relay(Socket console) => new(Loopback(), (IPEndPoint)console.LocalEndPoint!, null, _ => true);

    private static IPEndPoint Loopback() => new(IPAddress.Loopback, 0);

    private Socket Udp()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(Loopback());
        _sockets.Add(socket);
        return socket;
    }

    private static async Task<(byte[] Data, IPEndPoint Sender)> ReceiveAsync(Socket socket)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var buffer = new byte[2048];
        var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
        return (buffer[..result.ReceivedBytes], (IPEndPoint)result.RemoteEndPoint);
    }

    private static async Task<bool> ArrivesAsync(Socket socket)
    {
        using var timeout = new CancellationTokenSource(Silence);
        try
        {
            await socket.ReceiveFromAsync(new byte[2048], SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
