using System.Net;
using System.Text;
using Cord.Protocol.Discovery;

namespace Cord.Protocol.Tests.Discovery;

public class DiscoveryMessagesTests
{
    private static readonly IPAddress Sender = IPAddress.Parse("192.168.1.50");

    [Fact]
    public void Search_IsNulTerminatedPs5Request()
    {
        var expected = "SRCH * HTTP/1.1\ndevice-discovery-protocol-version:00030010\n\0";

        Assert.Equal(expected, Encoding.ASCII.GetString(DiscoveryMessages.Search()));
    }

    [Fact]
    public void Wakeup_ContainsDecimalCredential()
    {
        var expected =
            "WAKEUP * HTTP/1.1\n"
            + "client-type:vr\n"
            + "auth-type:R\n"
            + "model:w\n"
            + "app-type:r\n"
            + "user-credential:439041101\n"
            + "device-discovery-protocol-version:00030010\n\0";

        Assert.Equal(expected, Encoding.ASCII.GetString(DiscoveryMessages.Wakeup(439041101)));
    }

    [Fact]
    public void TryParseResponse_ReadyConsole()
    {
        var datagram = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 Ok\n"
            + "host-id:ABCDEF012345\n"
            + "host-type:PS5\n"
            + "host-name:Living Room\n"
            + "host-request-port:997\n"
            + "device-discovery-protocol-version:00030010\n"
            + "system-version:09400004\n"
            + "running-app-titleid:PPSA01234\n"
            + "running-app-name:Some Game\n");

        Assert.True(DiscoveryMessages.TryParseResponse(datagram, Sender, out var console));

        Assert.NotNull(console);
        Assert.Equal(Sender, console.Address);
        Assert.Equal(ConsoleState.Ready, console.State);
        Assert.Equal("ABCDEF012345", console.HostId);
        Assert.Equal("PS5", console.HostType);
        Assert.Equal("Living Room", console.HostName);
        Assert.Equal(997, console.RequestPort);
        Assert.Equal("PPSA01234", console.RunningAppTitleId);
        Assert.Equal("Some Game", console.RunningAppName);
        Assert.True(console.IsPs5);
        Assert.True(console.SupportsRemotePlay);
    }

    [Fact]
    public void TryParseResponse_StandbyConsoleWithCrLfAndTrailingNul()
    {
        var datagram = Encoding.ASCII.GetBytes(
            "HTTP/1.1 620 Server Standby\r\n"
            + "host-id:ABCDEF012345\r\n"
            + "device-discovery-protocol-version:00030010\r\n\0garbage");

        Assert.True(DiscoveryMessages.TryParseResponse(datagram, Sender, out var console));

        Assert.Equal(ConsoleState.Standby, console!.State);
        Assert.Equal("ABCDEF012345", console.HostId);
        Assert.Null(console.RunningAppName);
    }

    [Fact]
    public void TryParseResponse_UnknownStatus()
    {
        var datagram = Encoding.ASCII.GetBytes("HTTP/1.1 500 Error\n");

        Assert.True(DiscoveryMessages.TryParseResponse(datagram, Sender, out var console));
        Assert.Equal(ConsoleState.Unknown, console!.State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SRCH * HTTP/1.1\n")]
    [InlineData("HTTP/1.1 abc\n")]
    [InlineData("garbage")]
    public void TryParseResponse_RejectsInvalidStatusLine(string text)
    {
        Assert.False(DiscoveryMessages.TryParseResponse(Encoding.ASCII.GetBytes(text), Sender, out var console));
        Assert.Null(console);
    }

    [Theory]
    [InlineData("00020020", "09400004", false, false)]
    [InlineData("00030010", "08050000", true, false)]
    [InlineData("00030010", "08050001", true, true)]
    [InlineData("00030010", null, true, false)]
    public void SupportsRemotePlay_RequiresPs5AndMinimumVersion(string protocol, string? systemVersion, bool isPs5, bool supported)
    {
        var console = new DiscoveredConsole(Sender, ConsoleState.Ready, null, null, null, systemVersion, protocol, null, null, null);

        Assert.Equal(isPs5, console.IsPs5);
        Assert.Equal(supported, console.SupportsRemotePlay);
    }
}
