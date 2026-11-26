using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Cord.Protocol.Http;

/// <summary>A TCP connection exchanging the console's HTTP-like requests and responses.</summary>
public sealed class RpHttpConnection : IAsyncDisposable
{
    private const int MaxHeaderSize = 8 * 1024;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private byte[] _pending = [];

    private RpHttpConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public NetworkStream Stream => _stream;

    public static async Task<RpHttpConnection> ConnectAsync(IPEndPoint endPoint, CancellationToken cancellationToken)
    {
        var client = new TcpClient(endPoint.AddressFamily) { NoDelay = true };
        try
        {
            await client.ConnectAsync(endPoint, cancellationToken);
            return new RpHttpConnection(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task SendAsync(string head, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken);
        if (!body.IsEmpty)
            await _stream.WriteAsync(body, cancellationToken);
    }

    /// <summary>
    /// Reads a response header block. Bytes received after it are kept and returned first by
    /// <see cref="ReadBodyAsync"/> and <see cref="TakePending"/>.
    /// </summary>
    public async Task<RpHttpResponse> ReadResponseAsync(CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[1024];
        while (true)
        {
            var headerEnd = RpHttpResponse.FindHeaderEnd(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            if (headerEnd >= 0)
            {
                var data = buffer.ToArray();
                _pending = data[headerEnd..];
                return RpHttpResponse.Parse(data.AsSpan(0, headerEnd));
            }

            if (buffer.Length > MaxHeaderSize)
                throw new InvalidDataException("Response header is too large.");

            var read = await _stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                throw new IOException("Connection closed before the response header was complete.");
            buffer.Write(chunk, 0, read);
        }
    }

    public async Task<byte[]> ReadBodyAsync(RpHttpResponse response, CancellationToken cancellationToken)
    {
        if (!int.TryParse(response.Header("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length <= 0)
            throw new InvalidDataException("Response has no body.");

        var body = new byte[length];
        var filled = Math.Min(_pending.Length, length);
        _pending.AsSpan(0, filled).CopyTo(body);
        _pending = _pending[filled..];
        await _stream.ReadExactlyAsync(body.AsMemory(filled), cancellationToken);
        return body;
    }

    public byte[] TakePending()
    {
        var pending = _pending;
        _pending = [];
        return pending;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            _client.Client.Shutdown(SocketShutdown.Both);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
        }

        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
