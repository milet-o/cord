using System.Net;

namespace Cord.Protocol.Discovery;

public sealed record DiscoveredConsole(
    IPAddress Address,
    ConsoleState State,
    string? HostId,
    string? HostName,
    string? HostType,
    string? SystemVersion,
    string? ProtocolVersion,
    int? RequestPort,
    string? RunningAppTitleId,
    string? RunningAppName)
{
    public bool IsPs5 => ProtocolVersion == DiscoveryProtocol.ProtocolVersion;

    public bool SupportsRemotePlay =>
        IsPs5
        && int.TryParse(SystemVersion, out var version)
        && version >= DiscoveryProtocol.MinimumRemotePlaySystemVersion;
}
