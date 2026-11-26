using System.Net;

namespace Cord.Relay.Tests;

public sealed class RelayClientFilterTests
{
    private static readonly IPAddress Console = IPAddress.Parse("192.168.0.130");

    private readonly RelayClientFilter _filter = new(Console,
    [
        new(IPAddress.Parse("192.168.0.62"), IPAddress.Parse("255.255.255.0")),
        new(IPAddress.Parse("26.50.127.143"), IPAddress.Parse("255.0.0.0")),
        new(IPAddress.Parse("100.109.163.35"), IPAddress.Parse("255.255.255.255")),
        new(IPAddress.Parse("169.254.10.20"), IPAddress.Parse("255.255.0.0")),
        new(IPAddress.Parse("172.30.128.1"), IPAddress.Parse("255.255.240.0"), HostOnly: true),
    ]);

    [Theory]
    [InlineData("26.12.34.56")]
    [InlineData("100.80.1.2")]
    [InlineData("::ffff:26.12.34.56")]
    public void AcceptsClientsFromOtherNetworks(string client)
    {
        Assert.True(_filter.Accepts(IPAddress.Parse(client)));
    }

    [Theory]
    [InlineData("192.168.0.20")]
    [InlineData("192.168.0.62")]
    [InlineData("26.50.127.143")]
    [InlineData("172.30.128.1")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void RejectsTheConsoleNetworkAndThisComputer(string client)
    {
        Assert.False(_filter.Accepts(IPAddress.Parse(client)));
    }

    [Fact]
    public void SharesTheReachableAddressesOutsideTheConsoleNetwork()
    {
        Assert.Equal([IPAddress.Parse("26.50.127.143"), IPAddress.Parse("100.109.163.35")], _filter.ShareAddresses);
    }
}
