using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Cord.Protocol.Discovery;

public sealed class DiscoveryClient : IDisposable
{
    private const int ReceiveBufferSize = 1024;

    private readonly Socket _socket;
    private readonly int _consolePort;

    private DiscoveryClient(Socket socket, int consolePort)
    {
        _socket = socket;
        _consolePort = consolePort;
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    public static DiscoveryClient Create() => Create(DiscoveryProtocol.ConsolePort);

    internal static DiscoveryClient Create(int consolePort)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            EnableBroadcast = true,
        };

        try
        {
            BindPreferredPort(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new DiscoveryClient(socket, consolePort);
    }

    public ValueTask SendSearchAsync(IPAddress target, CancellationToken cancellationToken = default) =>
        SendAsync(DiscoveryMessages.Search(), target, cancellationToken);

    public ValueTask SendWakeupAsync(IPAddress target, ulong credential, CancellationToken cancellationToken = default) =>
        SendAsync(DiscoveryMessages.Wakeup(credential), target, cancellationToken);

    public async IAsyncEnumerable<DiscoveredConsole> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new byte[ReceiveBufferSize];
        EndPoint anyEndPoint = new IPEndPoint(IPAddress.Any, 0);

        while (true)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, anyEndPoint, cancellationToken);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Windows reports ICMP port unreachable from an earlier send as a receive error.
                continue;
            }

            var sender = ((IPEndPoint)result.RemoteEndPoint).Address;
            if (DiscoveryMessages.TryParseResponse(buffer.AsSpan(0, result.ReceivedBytes), sender, out var console))
                yield return console!;
        }
    }

    public void Dispose() => _socket.Dispose();

    private async ValueTask SendAsync(byte[] message, IPAddress target, CancellationToken cancellationToken) =>
        await _socket.SendToAsync(message, SocketFlags.None, new IPEndPoint(target, _consolePort), cancellationToken);

    private static void BindPreferredPort(Socket socket)
    {
        for (var port = DiscoveryProtocol.LocalPortMin; port <= DiscoveryProtocol.LocalPortMax; port++)
        {
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                return;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
            }
        }

        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
    }
}
