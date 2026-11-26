using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Cord.Protocol.Net;

namespace Cord.Relay;

/// <summary>An IPv4 address of this computer. Host-only adapters (Hyper-V, WSL, Docker) cannot be reached from other computers.</summary>
internal readonly record struct LocalInterface(IPAddress Address, IPAddress Mask, bool HostOnly = false);

/// <summary>
/// Decides which clients the relay serves and which local addresses they can use to reach it.
/// Clients on the console's own network reach it directly. Relaying their discovery broadcasts,
/// or those of this computer, would make the console show up at a second address.
/// </summary>
internal sealed class RelayClientFilter
{
    private static readonly string[] HostOnlyNamePrefixes = ["docker", "br-", "virbr", "veth"];

    private readonly IPAddress _console;
    private readonly IReadOnlyList<LocalInterface> _interfaces;
    private readonly IPAddress? _consoleMask;

    public RelayClientFilter(IPAddress console, IReadOnlyList<LocalInterface> interfaces)
    {
        _console = console;
        _interfaces = interfaces;
        _consoleMask = interfaces.FirstOrDefault(i => LocalNetwork.SameSubnet(i.Address, console, i.Mask)).Mask;
        ShareAddresses = [.. interfaces.Where(i => !i.HostOnly && !OnConsoleNetwork(i.Address) && !IsLinkLocal(i.Address)).Select(i => i.Address)];
    }

    /// <inheritdoc cref="IConsoleRelay.ShareAddresses"/>
    public IReadOnlyList<IPAddress> ShareAddresses { get; }

    public static RelayClientFilter ForThisComputer(IPAddress console) => new(console, [.. LocalInterfaces()]);

    public bool Accepts(IPAddress client)
    {
        if (client.IsIPv4MappedToIPv6)
            client = client.MapToIPv4();
        return client.AddressFamily == AddressFamily.InterNetwork
            && !IPAddress.IsLoopback(client)
            && !_interfaces.Any(i => i.Address.Equals(client))
            && !OnConsoleNetwork(client);
    }

    private bool OnConsoleNetwork(IPAddress address) => _consoleMask is not null && LocalNetwork.SameSubnet(address, _console, _consoleMask);

    private static bool IsLinkLocal(IPAddress address) => address.GetAddressBytes() is [169, 254, ..];

    private static IEnumerable<LocalInterface> LocalInterfaces()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            var hostOnly = IsHostOnly(networkInterface);
            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && unicast.IPv4Mask is not null)
                    yield return new LocalInterface(unicast.Address, unicast.IPv4Mask, hostOnly);
            }
        }
    }

    private static bool IsHostOnly(NetworkInterface networkInterface) =>
        networkInterface.Description.Contains("Hyper-V Virtual Ethernet", StringComparison.OrdinalIgnoreCase)
        || HostOnlyNamePrefixes.Any(prefix => networkInterface.Name.StartsWith(prefix, StringComparison.Ordinal));
}
