using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Cord.Protocol.Net;

/// <summary>IPv4 broadcast addresses of the local interfaces.</summary>
public static class LocalNetwork
{
    /// <summary>
    /// Directed broadcast address of every active IPv4 interface. The limited broadcast address
    /// (255.255.255.255) may leave through a single interface only, so both are needed.
    /// </summary>
    public static IEnumerable<IPAddress> DirectedBroadcastAddresses() =>
        Ipv4Interfaces().Select(i => BroadcastAddress(i.Address, i.Mask));

    /// <summary>Directed broadcast address of the interface whose subnet contains <paramref name="target"/>.</summary>
    public static IPAddress? BroadcastAddressFor(IPAddress target)
    {
        if (target.AddressFamily != AddressFamily.InterNetwork)
            return null;
        foreach (var (address, mask) in Ipv4Interfaces())
        {
            if (SameSubnet(address, target, mask))
                return BroadcastAddress(address, mask);
        }

        return null;
    }

    public static IPAddress BroadcastAddress(IPAddress address, IPAddress mask)
    {
        var bytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] |= (byte)~maskBytes[i];
        return new IPAddress(bytes);
    }

    public static bool SameSubnet(IPAddress first, IPAddress second, IPAddress mask)
    {
        var a = first.GetAddressBytes();
        var b = second.GetAddressBytes();
        var m = mask.GetAddressBytes();
        for (var i = 0; i < a.Length; i++)
        {
            if ((a[i] & m[i]) != (b[i] & m[i]))
                return false;
        }

        return true;
    }

    private static IEnumerable<(IPAddress Address, IPAddress Mask)> Ipv4Interfaces()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && unicast.IPv4Mask is not null)
                    yield return (unicast.Address, unicast.IPv4Mask);
            }
        }
    }
}
