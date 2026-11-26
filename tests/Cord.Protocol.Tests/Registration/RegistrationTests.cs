using System.Net;
using System.Net.Sockets;
using System.Text;
using Cord.Protocol.Crypto;
using Cord.Protocol.Http;
using Cord.Protocol.Registration;
using Cord.Protocol.Tests.Support;
using static Cord.Protocol.Tests.Support.TestBytes;

namespace Cord.Protocol.Tests.Registration;

public class RegistrationTests
{
    private const uint Pin = 12345678;
    private static readonly byte[] Ambassador = Sequence(16, i => (i * 7) + 1);
    private static readonly PsnAccountId Account = PsnAccountId.FromBase64(Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]));

    [Fact]
    public void BuildPayload_MatchesReference()
    {
        var (payload, cipher) = RegistrationMessages.BuildPayload(TestTables.Value, Ambassador, Account, Pin);
        cipher.Dispose();

        Assert.Equal(587, payload.Length);
        Assert.Equal(Hex("36df5ac733a9cad1"), payload[0xC7..0xCF]);
        Assert.Equal(Hex("7c13437911b00536"), payload[0x191..0x199]);
        Assert.Equal(
            Hex("58a576f4c10f3ee09c73b37847afe60383e8378d30159087519d25bc7c99ccc8cfc1c541ba2b6f9cda8874fdad423fdfdb78e3a1f110c97b5f5c0b304f56b33985f9bb640e3da54e7734777285d81ae2ae6fe8490a7fc999a5b9a29c7932f6673c35a7a506e70615e147e7"),
            payload[0x1E0..]);
    }

    [Fact]
    public void ParseResponseBody_ReadsCredentials()
    {
        var body = Encoding.ASCII.GetBytes(
            "AP-Ssid: x\r\n"
            + "PS5-Mac: 0123456789ab\r\n"
            + "PS5-RegistKey: 3161326233633464\r\n"
            + "RP-KeyType: 2\r\n"
            + "RP-Key: 00112233445566778899aabbccddeeff\r\n"
            + "PS5-Nickname: Living Room\r\n");

        var console = RegistrationMessages.ParseResponseBody(body);

        Assert.Equal("1a2b3c4d", Encoding.ASCII.GetString(console.RegistKey).TrimEnd('\0'));
        Assert.Equal(16, console.RegistKey.Length);
        Assert.Equal(Hex("00112233445566778899aabbccddeeff"), console.RpKey);
        Assert.Equal(2, console.RpKeyType);
        Assert.Equal(Hex("0123456789ab"), console.MacAddress);
        Assert.Equal("Living Room", console.Nickname);
    }

    [Fact]
    public void ParseResponseBody_RejectsMissingKeys()
    {
        var body = Encoding.ASCII.GetBytes("PS5-Mac: 0123456789ab\r\n");

        var e = Assert.Throws<RemotePlayException>(() => RegistrationMessages.ParseResponseBody(body));
        Assert.Equal(RemotePlayError.InvalidResponse, e.Error);
    }

    [Fact]
    public async Task RegisterAsync_CompletesExchangeWithConsole()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var console = new FakeRegistrationConsole(statusLine: "HTTP/1.1 200 OK");
        var consoleTask = console.RunAsync(timeout.Token);
        var client = new RegistrationClient(TestTables.Value, console.Port, () => Ambassador);

        var registered = await client.RegisterAsync(IPAddress.Loopback, Account, Pin, timeout.Token);

        Assert.Equal(Hex("00112233445566778899aabbccddeeff"), registered.RpKey);
        Assert.Equal("Test PS5", registered.Nickname);
        var request = await consoleTask;
        Assert.StartsWith("POST /sie/ps5/rp/sess/rgst HTTP/1.1\r\n HTTP/1.1\r\n", request.Head);
        Assert.Contains("Np-AccountId: AQIDBAUGBwg=", request.InnerHeader);
    }

    [Fact]
    public async Task RegisterAsync_ReportsWrongPin()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var console = new FakeRegistrationConsole(statusLine: "HTTP/1.1 403 Forbidden", reason: "80108b09");
        _ = console.RunAsync(timeout.Token);
        var client = new RegistrationClient(TestTables.Value, console.Port, () => Ambassador);

        var e = await Assert.ThrowsAsync<RemotePlayException>(() => client.RegisterAsync(IPAddress.Loopback, Account, Pin, timeout.Token));

        Assert.Equal(RemotePlayError.RegistrationFailed, e.Error);
    }

    private sealed class FakeRegistrationConsole : IDisposable
    {
        private readonly TcpListener _tcp;
        private readonly UdpClient _udp;
        private readonly string _statusLine;
        private readonly string? _reason;

        public FakeRegistrationConsole(string statusLine, string? reason = null)
        {
            _statusLine = statusLine;
            _reason = reason;
            // UDP and TCP must share a port; Windows reserves whole port blocks for one protocol,
            // so try random ports instead of consecutive ephemeral ones.
            for (var attempt = 0; ; attempt++)
            {
                var port = Random.Shared.Next(20000, 60000);
                UdpClient? udp = null;
                try
                {
                    udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                    var tcp = new TcpListener(IPAddress.Loopback, port);
                    tcp.Start();
                    (_udp, _tcp, Port) = (udp, tcp, port);
                    break;
                }
                catch (SocketException) when (attempt < 200)
                {
                    udp?.Dispose();
                }
            }
        }

        public int Port { get; }

        public async Task<(string Head, string InnerHeader)> RunAsync(CancellationToken cancellationToken)
        {
            var search = await _udp.ReceiveAsync(cancellationToken);
            Assert.Equal("SRC3\0"u8.ToArray(), search.Buffer);
            await _udp.SendAsync("RES3 ok"u8.ToArray(), search.RemoteEndPoint, cancellationToken);

            using var client = await _tcp.AcceptTcpClientAsync(cancellationToken);
            var stream = client.GetStream();
            var received = new MemoryStream();
            var buffer = new byte[4096];
            int headEnd;
            while ((headEnd = RpHttpResponse.FindHeaderEnd(received.ToArray())) < 0)
                received.Write(buffer, 0, await stream.ReadAsync(buffer, cancellationToken));

            var head = Encoding.ASCII.GetString(received.ToArray(), 0, headEnd);
            var lengthLine = head.Split("\r\n").Single(l => l.StartsWith("Content-Length:", StringComparison.Ordinal));
            var length = int.Parse(lengthLine["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            while (received.Length < headEnd + length)
                received.Write(buffer, 0, await stream.ReadAsync(buffer, cancellationToken));
            var payload = received.ToArray()[headEnd..];

            using var cipher = RpCipher.ForRegistration(TestTables.Value, Ambassador, payload[0x18D] & 0x1F, Pin);
            var inner = Encoding.ASCII.GetString(cipher.Decrypt(0, payload.AsSpan(0x1E0)));

            if (_reason is not null)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"{_statusLine}\r\nRP-Application-Reason: {_reason}\r\n\r\n"), cancellationToken);
                return (head, inner);
            }

            var body = cipher.Encrypt(0, Encoding.ASCII.GetBytes(
                "PS5-Mac: 0123456789ab\r\nPS5-RegistKey: 3161326233633464\r\nRP-KeyType: 2\r\n"
                + "RP-Key: 00112233445566778899aabbccddeeff\r\nPS5-Nickname: Test PS5\r\n"));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"{_statusLine}\r\nContent-Length: {body.Length}\r\n\r\n"), cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
            return (head, inner);
        }

        public void Dispose()
        {
            _tcp.Stop();
            _udp.Dispose();
        }
    }
}
