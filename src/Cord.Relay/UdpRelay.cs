using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Cord.Relay;

/// <summary>
/// Forwards datagrams between remote clients and one console port. Each client gets its own
/// upstream socket, so the console's replies can be sent back to the client that caused them.
/// </summary>
internal sealed class UdpRelay : IDisposable
{
    private const int BufferSize = 64 * 1024;
    private const int SocketBufferSize = 4 * 1024 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    private readonly Socket _listener;
    private readonly SocketAddress _console;
    private readonly SocketAddress? _broadcast;
    private readonly Func<IPAddress, bool> _accepts;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<SocketAddress, Mapping> _mappings = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ITimer _sweep;

    /// <param name="broadcast">
    /// Also sends every datagram here. Needed for the registration search, which a console in
    /// pairing mode only answers when it arrives as a broadcast.
    /// </param>
    public UdpRelay(IPEndPoint listen, IPEndPoint console, IPEndPoint? broadcast, Func<IPAddress, bool> accepts, TimeProvider? time = null)
    {
        _console = console.Serialize();
        _broadcast = broadcast?.Serialize();
        _accepts = accepts;
        _time = time ?? TimeProvider.System;
        _listener = CreateSocket(listen.AddressFamily, enableBroadcast: false);
        try
        {
            _listener.Bind(listen);
        }
        catch
        {
            _listener.Dispose();
            throw;
        }

        _ = Task.Run(() => ReceiveFromClientsAsync(_stop.Token));
        _sweep = _time.CreateTimer(_ => RemoveIdle(), null, SweepInterval, SweepInterval);
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndPoint!;

    public IEnumerable<IPAddress> Clients => _mappings.Values.Select(m => m.Client.Address);

    public void Dispose()
    {
        _stop.Cancel();
        _sweep.Dispose();
        _listener.Dispose();
        foreach (var mapping in _mappings.Values)
            mapping.Dispose();
        _mappings.Clear();
        _stop.Dispose();
    }

    internal void RemoveIdle()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, mapping) in _mappings)
        {
            if (now - mapping.LastActivity > IdleTimeout && _mappings.TryRemove(key, out _))
                mapping.Dispose();
        }
    }

    private async Task ReceiveFromClientsAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        var sender = new SocketAddress(_listener.AddressFamily);
        while (!cancellationToken.IsCancellationRequested)
        {
            int received;
            try
            {
                received = await _listener.ReceiveFromAsync(buffer, SocketFlags.None, sender, cancellationToken);
            }
            catch (SocketException e) when (IsTransient(e))
            {
                continue;
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            if (!_mappings.TryGetValue(sender, out var mapping) && (mapping = TryAddMapping(sender)) is null)
                continue;
            mapping.Touch(_time.GetUtcNow());
            await mapping.SendToConsoleAsync(buffer.AsMemory(0, received), cancellationToken);
        }
    }

    private Mapping? TryAddMapping(SocketAddress sender)
    {
        var client = (IPEndPoint)new IPEndPoint(IPAddress.Any, 0).Create(sender);
        if (!_accepts(client.Address))
            return null;

        var key = new SocketAddress(sender.Family, sender.Size);
        sender.Buffer.CopyTo(key.Buffer);
        var upstream = CreateSocket(client.AddressFamily, enableBroadcast: _broadcast is not null);
        try
        {
            upstream.Bind(new IPEndPoint(IPAddress.Any, 0));
        }
        catch (SocketException)
        {
            upstream.Dispose();
            return null;
        }

        var mapping = new Mapping(this, key, client, upstream);
        _mappings[key] = mapping;
        _ = Task.Run(() => mapping.ReceiveFromConsoleAsync(_stop.Token));
        return mapping;
    }

    private static Socket CreateSocket(AddressFamily family, bool enableBroadcast) =>
        new(family, SocketType.Dgram, ProtocolType.Udp)
        {
            EnableBroadcast = enableBroadcast,
            ReceiveBufferSize = SocketBufferSize,
            SendBufferSize = SocketBufferSize,
        };

    // Windows reports an ICMP port unreachable caused by an earlier send as a receive error.
    private static bool IsTransient(SocketException e) =>
        e.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.MessageSize;

    private sealed class Mapping(UdpRelay relay, SocketAddress key, IPEndPoint client, Socket upstream) : IDisposable
    {
        private long _lastActivityTicks = relay._time.GetUtcNow().UtcTicks;

        public IPEndPoint Client { get; } = client;

        public DateTimeOffset LastActivity => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

        public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastActivityTicks, now.UtcTicks);

        public async ValueTask SendToConsoleAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
        {
            try
            {
                await upstream.SendToAsync(datagram, SocketFlags.None, relay._console, cancellationToken);
                if (relay._broadcast is { } broadcast)
                    await upstream.SendToAsync(datagram, SocketFlags.None, broadcast, cancellationToken);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }

        public async Task ReceiveFromConsoleAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[BufferSize];
            var sender = new SocketAddress(upstream.AddressFamily);
            while (!cancellationToken.IsCancellationRequested)
            {
                int received;
                try
                {
                    received = await upstream.ReceiveFromAsync(buffer, SocketFlags.None, sender, cancellationToken);
                }
                catch (SocketException e) when (IsTransient(e))
                {
                    continue;
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                if (!sender.Equals(relay._console))
                    continue;
                Touch(relay._time.GetUtcNow());
                await SendToClientAsync(buffer.AsMemory(0, received), cancellationToken);
            }
        }

        public void Dispose() => upstream.Dispose();

        private async ValueTask SendToClientAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
        {
            try
            {
                await relay._listener.SendToAsync(datagram, SocketFlags.None, key, cancellationToken);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }
}
