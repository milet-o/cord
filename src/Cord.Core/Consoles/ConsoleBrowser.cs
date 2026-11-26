using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Cord.Protocol.Discovery;
using Cord.Protocol.Net;

namespace Cord.Core.Consoles;

/// <summary>A console seen on the network, possibly also registered.</summary>
public sealed record ConsoleEntry(
    string HostId,
    string Name,
    IPAddress? Address,
    ConsoleState State,
    bool Registered,
    string? RunningApp,
    DateTimeOffset LastSeen);

/// <summary>
/// Periodically searches the local network for consoles and merges the results with the
/// registered ones (docs/protocol/01-discovery.md).
/// </summary>
public sealed class ConsoleBrowser : IAsyncDisposable
{
    private static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(10);

    private readonly ConsoleRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DiscoveredConsole> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<IPAddress, byte> _manualAddresses = new();
    private readonly CancellationTokenSource _stop = new();
    private DiscoveryClient? _client;
    private Task? _receive;
    private Task? _search;

    public ConsoleBrowser(ConsoleRegistry registry, TimeProvider? time = null)
    {
        _registry = registry;
        _time = time ?? TimeProvider.System;
    }

    public void Start()
    {
        _client = DiscoveryClient.Create();
        _receive = Task.Run(() => ReceiveAsync(_client, _stop.Token));
        _search = Task.Run(() => SearchAsync(_client, _stop.Token));
    }

    /// <summary>Also searches this address directly (consoles on other subnets or without broadcast).</summary>
    public void AddManualAddress(IPAddress address) => _manualAddresses.TryAdd(address, 0);

    public IReadOnlyList<ConsoleEntry> Snapshot()
    {
        var now = _time.GetUtcNow();
        var entries = new Dictionary<string, ConsoleEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var stored in _registry.Consoles)
        {
            _ = IPAddress.TryParse(stored.LastAddress, out var address);
            entries[stored.HostId] = new ConsoleEntry(stored.HostId, stored.Nickname, address, ConsoleState.Unknown, true, null, DateTimeOffset.MinValue);
        }

        foreach (var (hostId, console) in _seen)
        {
            var seenAt = _seenAt.GetValueOrDefault(hostId);
            if (now - seenAt > Expiry)
                continue;
            var registered = entries.TryGetValue(hostId, out var existing);
            entries[hostId] = new ConsoleEntry(
                hostId,
                registered ? existing!.Name : console.HostName ?? hostId,
                console.Address,
                console.State,
                registered,
                console.RunningAppName,
                seenAt);
        }

        return [.. entries.Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    internal void Observe(DiscoveredConsole console)
    {
        if (!console.IsPs5 || console.HostId is null)
            return;
        _seen[console.HostId] = console;
        _seenAt[console.HostId] = _time.GetUtcNow();
        _registry.UpdateAddress(console.HostId, console.Address.ToString());
    }

    public async Task WakeAsync(IPAddress address, ulong credential, CancellationToken cancellationToken = default)
    {
        var client = _client ?? throw new InvalidOperationException("The browser is not started.");
        await client.SendWakeupAsync(address, credential, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _client?.Dispose();
        foreach (var task in new[] { _receive, _search })
        {
            if (task is null)
                continue;
            try
            {
                await task;
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task ReceiveAsync(DiscoveryClient client, CancellationToken cancellationToken)
    {
        await foreach (var console in client.ReceiveAsync(cancellationToken))
            Observe(console);
    }

    private async Task SearchAsync(DiscoveryClient client, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SearchInterval);
        do
        {
            foreach (var target in Targets())
            {
                try
                {
                    await client.SendSearchAsync(target, cancellationToken);
                }
                catch (SocketException)
                {
                }
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private HashSet<IPAddress> Targets()
    {
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var address in LocalNetwork.DirectedBroadcastAddresses())
            targets.Add(address);
        foreach (var address in _manualAddresses.Keys)
            targets.Add(address);
        foreach (var stored in _registry.Consoles)
        {
            if (IPAddress.TryParse(stored.LastAddress, out var address))
                targets.Add(address);
        }

        return targets;
    }
}
