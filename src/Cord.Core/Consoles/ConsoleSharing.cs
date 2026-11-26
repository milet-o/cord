using System.Net;
using Cord.Relay;

namespace Cord.Core.Consoles;

/// <summary>
/// Shares one paired console with Cord on other networks through a relay on this computer,
/// following the console when its address changes. Only one console can be shared at a time
/// because the relay listens on the fixed Remote Play ports.
/// </summary>
public sealed class ConsoleSharing : IDisposable
{
    private readonly Func<IPAddress, IConsoleRelay> _start;
    private IConsoleRelay? _relay;

    public ConsoleSharing()
        : this(ConsoleRelay.Start)
    {
    }

    internal ConsoleSharing(Func<IPAddress, IConsoleRelay> start) => _start = start;

    /// <summary>The shared console, or null.</summary>
    public string? HostId { get; private set; }

    /// <summary>Why the last attempt to share failed, in words for the user.</summary>
    public string? Error { get; private set; }

    public IReadOnlyList<IPAddress> ShareAddresses => _relay?.ShareAddresses ?? [];

    public int ClientCount => _relay?.Clients.Count ?? 0;

    public void Share(ConsoleEntry entry)
    {
        Stop();
        Error = null;
        if (entry.Address is null)
        {
            Error = $"{entry.Name} was not found on your network, so it cannot be shared.";
            return;
        }

        try
        {
            _relay = _start(entry.Address);
            HostId = entry.HostId;
        }
        catch (RelayException e)
        {
            Error = e.Message;
        }
    }

    public void Stop()
    {
        _relay?.Dispose();
        _relay = null;
        HostId = null;
    }

    public void ClearError() => Error = null;

    /// <summary>Restarts the relay when the shared console moved to another address and stops it when the console was forgotten.</summary>
    public void Follow(IReadOnlyList<ConsoleEntry> entries)
    {
        if (HostId is null || _relay is null)
            return;
        var entry = entries.FirstOrDefault(e => e.Registered && string.Equals(e.HostId, HostId, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            Stop();
        else if (entry.Address is not null && !entry.Address.Equals(_relay.Console))
            Share(entry);
    }

    public void Dispose() => Stop();
}
