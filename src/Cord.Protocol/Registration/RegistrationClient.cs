using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Cord.Protocol.Crypto;
using Cord.Protocol.Http;
using Cord.Protocol.Net;

namespace Cord.Protocol.Registration;

/// <summary>Pairs the client with a console using its PIN (docs/protocol/04-registration.md).</summary>
public sealed class RegistrationClient
{
    public const int DefaultPort = 9295;

    private static readonly byte[] SearchRequest = "SRC3\0"u8.ToArray();
    private static readonly byte[] SearchResponsePrefix = "RES3"u8.ToArray();
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DelayBeforeRequest = TimeSpan.FromMilliseconds(100);

    private readonly RpCryptTables _tables;
    private readonly int _port;
    private readonly Func<byte[]> _createAmbassador;

    public RegistrationClient(RpCryptTables tables)
        : this(tables, DefaultPort, () => RandomNumberGenerator.GetBytes(RpCipher.KeySize))
    {
    }

    internal RegistrationClient(RpCryptTables tables, int port, Func<byte[]> createAmbassador)
    {
        _tables = tables;
        _port = port;
        _createAmbassador = createAmbassador;
    }

    public async Task<RegisteredConsole> RegisterAsync(
        IPAddress console,
        PsnAccountId accountId,
        uint pin,
        CancellationToken cancellationToken = default)
    {
        var consoleAddress = await SearchAsync(console, cancellationToken);
        await Task.Delay(DelayBeforeRequest, cancellationToken);

        var (payload, cipher) = RegistrationMessages.BuildPayload(_tables, _createAmbassador(), accountId, pin);
        using (cipher)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ResponseTimeout);

            await using var connection = await RpHttpConnection.ConnectAsync(new IPEndPoint(consoleAddress, _port), timeout.Token);
            await connection.SendAsync(RegistrationMessages.RequestHead(payload.Length), payload, timeout.Token);

            var response = await connection.ReadResponseAsync(timeout.Token);
            if (response.StatusCode != 200)
                throw RemotePlayException.FromApplicationReason(response.ApplicationReason, "Registration");

            var body = await connection.ReadBodyAsync(response, timeout.Token);
            return RegistrationMessages.ParseResponseBody(cipher.Decrypt(0, body));
        }
    }

    // A console in pairing mode ignores a unicast search and only answers a broadcast one, so the
    // search goes to the console, to its subnet's broadcast address and to the limited broadcast.
    private async Task<IPAddress> SearchAsync(IPAddress console, CancellationToken cancellationToken)
    {
        using var socket = new Socket(console.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.EnableBroadcast = true;
        socket.Bind(new IPEndPoint(console.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        foreach (var target in SearchTargets(console))
        {
            try
            {
                await socket.SendToAsync(SearchRequest, SocketFlags.None, new IPEndPoint(target, _port), cancellationToken);
            }
            catch (SocketException) when (!target.Equals(console))
            {
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SearchTimeout);
        var buffer = new byte[256];
        EndPoint any = new IPEndPoint(console.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        try
        {
            while (true)
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, timeout.Token);
                if (buffer.AsSpan(0, result.ReceivedBytes).StartsWith(SearchResponsePrefix))
                    return ((IPEndPoint)result.RemoteEndPoint).Address;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RemotePlayException(RemotePlayError.ConsoleNotFound, "The console did not answer the registration search. Is the Pair Device screen open on the PS5?");
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
        {
            throw new RemotePlayException(RemotePlayError.ConsoleNotFound, "Remote Play is not listening on the console.");
        }
    }

    private static IEnumerable<IPAddress> SearchTargets(IPAddress console)
    {
        yield return console;
        if (console.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(console))
            yield break;
        if (LocalNetwork.BroadcastAddressFor(console) is { } subnetBroadcast)
            yield return subnetBroadcast;
        yield return IPAddress.Broadcast;
    }
}
