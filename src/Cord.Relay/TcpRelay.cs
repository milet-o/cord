using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Cord.Relay;

/// <summary>Forwards every accepted TCP connection to one console port, byte for byte.</summary>
internal sealed class TcpRelay : IDisposable
{
    private const int BufferSize = 16 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly Socket _listener;
    private readonly IPEndPoint _console;
    private readonly Func<IPAddress, bool> _accepts;
    private readonly ConcurrentDictionary<Socket, IPAddress> _clients = new();
    private readonly CancellationTokenSource _stop = new();

    public TcpRelay(IPEndPoint listen, IPEndPoint console, Func<IPAddress, bool> accepts)
    {
        _console = console;
        _accepts = accepts;
        _listener = new Socket(listen.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            _listener.Bind(listen);
            _listener.Listen();
        }
        catch
        {
            _listener.Dispose();
            throw;
        }

        _ = Task.Run(() => AcceptAsync(_stop.Token));
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndPoint!;

    public IEnumerable<IPAddress> Clients => _clients.Values;

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Dispose();
        foreach (var client in _clients.Keys)
            client.Dispose();
        _stop.Dispose();
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(cancellationToken);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            var address = ((IPEndPoint)client.RemoteEndPoint!).Address;
            if (!_accepts(address))
            {
                client.Dispose();
                continue;
            }

            _clients[client] = address;
            _ = Task.Run(() => ForwardAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task ForwardAsync(Socket client, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        using var upstream = new Socket(_console.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(ConnectTimeout);
                await upstream.ConnectAsync(_console, timeout.Token);
            }

            await Task.WhenAll(PumpAsync(client, upstream, connection), PumpAsync(upstream, client, connection));
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Dispose();
        }
    }

    // Ends with a half-close so a peer that finished sending still gets the other side's reply;
    // any failure tears down both directions.
    private static async Task PumpAsync(Socket from, Socket to, CancellationTokenSource connection)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await from.ReceiveAsync(buffer, SocketFlags.None, connection.Token)) > 0)
                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, connection.Token);
            to.Shutdown(SocketShutdown.Send);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            await connection.CancelAsync();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
