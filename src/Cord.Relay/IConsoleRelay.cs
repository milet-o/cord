using System.Net;

namespace Cord.Relay;

/// <summary>A running relay for one console.</summary>
public interface IConsoleRelay : IDisposable
{
    IPAddress Console { get; }

    /// <summary>Addresses of this computer outside the console's network, for clients to connect to.</summary>
    IReadOnlyList<IPAddress> ShareAddresses { get; }

    /// <summary>Remote clients with recent traffic or an open connection.</summary>
    IReadOnlyCollection<IPAddress> Clients { get; }
}
