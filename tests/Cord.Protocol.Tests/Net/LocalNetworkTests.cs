using System.Net;
using Cord.Protocol.Net;

namespace Cord.Protocol.Tests.Net;

public class LocalNetworkTests
{
    [Theory]
    [InlineData("192.168.0.62", "255.255.255.0", "192.168.0.255")]
    [InlineData("10.1.2.3", "255.255.0.0", "10.1.255.255")]
    [InlineData("26.50.127.143", "255.0.0.0", "26.255.255.255")]
    public void BroadcastAddress_SetsHostBits(string address, string mask, string expected)
    {
        Assert.Equal(IPAddress.Parse(expected), LocalNetwork.BroadcastAddress(IPAddress.Parse(address), IPAddress.Parse(mask)));
    }

    [Theory]
    [InlineData("192.168.0.62", "192.168.0.130", "255.255.255.0", true)]
    [InlineData("192.168.0.62", "192.168.1.130", "255.255.255.0", false)]
    [InlineData("100.109.163.35", "192.168.0.130", "255.255.255.255", false)]
    public void SameSubnet_ComparesNetworkBits(string first, string second, string mask, bool expected)
    {
        Assert.Equal(expected, LocalNetwork.SameSubnet(IPAddress.Parse(first), IPAddress.Parse(second), IPAddress.Parse(mask)));
    }

    [Fact]
    public void BroadcastAddressFor_IgnoresLoopbackAndIpv6()
    {
        Assert.Null(LocalNetwork.BroadcastAddressFor(IPAddress.Loopback));
        Assert.Null(LocalNetwork.BroadcastAddressFor(IPAddress.IPv6Loopback));
    }
}
