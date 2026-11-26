using System.Net;
using System.Net.Sockets;
using System.Text;
using Cord.Protocol.Discovery;

namespace Cord.Protocol.Tests.Discovery;

public class DiscoveryClientTests
{
    [Fact]
    public async Task SearchReachesConsoleAndResponseIsParsed()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var fakeConsole = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var consolePort = ((IPEndPoint)fakeConsole.Client.LocalEndPoint!).Port;
        using var client = DiscoveryClient.Create(consolePort);

        await client.SendSearchAsync(IPAddress.Loopback, timeout.Token);

        var request = await fakeConsole.ReceiveAsync(timeout.Token);
        Assert.Equal(DiscoveryMessages.Search(), request.Buffer);

        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 Ok\nhost-id:ABC\ndevice-discovery-protocol-version:00030010\n\0");
        await fakeConsole.SendAsync(response, request.RemoteEndPoint, timeout.Token);

        await foreach (var console in client.ReceiveAsync(timeout.Token))
        {
            Assert.Equal(IPAddress.Loopback, console.Address);
            Assert.Equal(ConsoleState.Ready, console.State);
            Assert.Equal("ABC", console.HostId);
            return;
        }
    }

    [Fact]
    public void Create_FallsBackToNextFreePortInRange()
    {
        using var first = DiscoveryClient.Create();
        using var second = DiscoveryClient.Create();

        Assert.NotEqual(first.LocalEndPoint.Port, second.LocalEndPoint.Port);
        Assert.InRange(second.LocalEndPoint.Port, 1, 65535);
    }
}
