using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Cord.Relay.Tests;

public sealed class TcpRelayTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly Socket _console = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

    public TcpRelayTests()
    {
        _console.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _console.Listen();
    }

    public void Dispose() => _console.Dispose();

    [Fact]
    public async Task ForwardsBothDirectionsAndPassesHalfCloseOn()
    {
        using var relay = Relay(_ => true);
        using var client = await ConnectAsync(relay);
        using var upstream = await AcceptAsync();

        await client.SendAsync(Encoding.ASCII.GetBytes("GET /sie/ps5/rp/sess/init HTTP/1.1\r\n\r\n"));
        client.Shutdown(SocketShutdown.Send);
        var request = await ReadToEndAsync(upstream);
        await upstream.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n\r\n"));
        upstream.Shutdown(SocketShutdown.Send);
        var response = await ReadToEndAsync(client);

        Assert.Equal("GET /sie/ps5/rp/sess/init HTTP/1.1\r\n\r\n", request);
        Assert.Equal("HTTP/1.1 200 OK\r\n\r\n", response);
    }

    [Fact]
    public async Task TracksConnectionsUntilBothSidesClose()
    {
        using var relay = Relay(_ => true);
        var client = await ConnectAsync(relay);
        var upstream = await AcceptAsync();
        await client.SendAsync(new byte[] { 1 });
        await ReadAsync(upstream, 1);

        Assert.Equal([IPAddress.Loopback], relay.Clients);

        client.Dispose();
        Assert.Equal("", await ReadToEndAsync(upstream));
        upstream.Dispose();
        await WaitUntilAsync(() => !relay.Clients.Any());
    }

    [Fact]
    public async Task ClosesConnectionsFromRejectedClients()
    {
        using var relay = Relay(_ => false);
        using var client = await ConnectAsync(relay);

        Assert.Equal("", await ReadToEndAsync(client));
        Assert.False(_console.Poll(TimeSpan.FromMilliseconds(300), SelectMode.SelectRead));
    }

    private TcpRelay Relay(Func<IPAddress, bool> accepts) =>
        new(new IPEndPoint(IPAddress.Loopback, 0), (IPEndPoint)_console.LocalEndPoint!, accepts);

    private static async Task<Socket> ConnectAsync(TcpRelay relay)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(relay.LocalEndPoint);
        return socket;
    }

    private async Task<Socket> AcceptAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        return await _console.AcceptAsync(timeout.Token);
    }

    private static async Task ReadAsync(Socket socket, int count)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
            read += await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, timeout.Token);
    }

    private static async Task<string> ReadToEndAsync(Socket socket)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var text = new StringBuilder();
        var buffer = new byte[1024];
        try
        {
            int read;
            while ((read = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token)) > 0)
                text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
        {
        }

        return text.ToString();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(20);
        }
    }
}
