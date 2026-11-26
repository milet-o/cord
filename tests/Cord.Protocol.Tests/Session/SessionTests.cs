using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cord.Protocol.Crypto;
using Cord.Protocol.Http;
using Cord.Protocol.Session;
using Cord.Protocol.Tests.Support;
using static Cord.Protocol.Tests.Support.TestBytes;

namespace Cord.Protocol.Tests.Session;

public class SessionTests
{
    private static readonly byte[] RegistKey = Pad16("1a2b3c4d");
    private static readonly byte[] Nonce = Sequence(16, i => 0xF0 - (i * 5));
    private static readonly byte[] Morning = Sequence(16, i => (i * 13) + 3);
    private const string ValidSessionId = "ABCDEFGHIJKLMNOPQRSTUVWXyz0123";

    [Fact]
    public void BuildRequest_SendsRegistKeyAsHexOfText()
    {
        var request = SessionRequest.BuildRequest(new IPEndPoint(IPAddress.Parse("10.0.0.2"), 9295), RegistKey);

        Assert.StartsWith("GET /sie/ps5/rp/sess/init HTTP/1.1\r\nHost: 10.0.0.2:9295\r\n", request);
        Assert.Contains("RP-Registkey: 3161326233633464\r\n", request);
        Assert.EndsWith("Rp-Version: 1.0\r\n\r\n", request);
    }

    [Fact]
    public async Task RequestNonceAsync_ReturnsNonce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = Listen(out var endPoint);
        var console = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await ReadHeadAsync(client.GetStream(), timeout.Token);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nRP-Nonce: {Convert.ToBase64String(Nonce)}\r\nRP-Version: 1.0\r\n\r\n"), timeout.Token);
        });

        var nonce = await SessionRequest.RequestNonceAsync(endPoint, RegistKey, timeout.Token);

        Assert.Equal(Nonce, nonce);
        await console;
        listener.Stop();
    }

    [Fact]
    public async Task RequestNonceAsync_MapsInUseError()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = Listen(out var endPoint);
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await ReadHeadAsync(client.GetStream(), timeout.Token);
            await client.GetStream().WriteAsync("HTTP/1.1 403 Forbidden\r\nRP-Application-Reason: 80108b10\r\n\r\n"u8.ToArray(), timeout.Token);
        });

        var e = await Assert.ThrowsAsync<RemotePlayException>(() => SessionRequest.RequestNonceAsync(endPoint, RegistKey, timeout.Token));

        Assert.Equal(RemotePlayError.InUse, e.Error);
        listener.Stop();
    }

    [Theory]
    [InlineData("J" + ValidSessionId, ValidSessionId)]
    [InlineData("JShort", null)]
    [InlineData("X" + ValidSessionId, null)]
    [InlineData("J" + ValidSessionId + "!", null)]
    public void ParseSessionId_ValidatesPayload(string payload, string? expected)
    {
        Assert.Equal(expected, CtrlChannel.ParseSessionId(Encoding.ASCII.GetBytes(payload)));
    }

    [Fact]
    public async Task Ctrl_AuthenticatesAnswersHeartbeatAndReceivesSessionId()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = Listen(out var endPoint);
        var secrets = SessionSecrets.Generate();
        using var clientCipher = RpCipher.ForSession(TestTables.Value, Nonce, Morning);
        using var consoleCipher = RpCipher.ForSession(TestTables.Value, Nonce, Morning);

        var console = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = client.GetStream();
            var head = await ReadHeadAsync(stream, timeout.Token);
            var headers = RpHttpResponse.ParseHeaderLines(head);

            Assert.Equal(RegistKey, consoleCipher.Decrypt(0, Convert.FromBase64String(headers["RP-Auth"])));
            Assert.Equal(secrets.DeviceId, consoleCipher.Decrypt(1, Convert.FromBase64String(headers["RP-Did"])));
            Assert.Equal("Win10.0.0\0"u8.ToArray(), consoleCipher.Decrypt(2, Convert.FromBase64String(headers["RP-OSType"])));
            Assert.Equal(new byte[] { 2, 0, 0, 0 }, consoleCipher.Decrypt(4, Convert.FromBase64String(headers["RP-StreamingType"])));

            var serverType = new byte[16];
            serverType[0] = 2;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nRP-Server-Type: {Convert.ToBase64String(consoleCipher.Encrypt(0, serverType))}\r\n\r\n"), timeout.Token);

            ulong consoleCounter = 1;
            await stream.WriteAsync(Frame(CtrlMessageType.HeartbeatRequest, []), timeout.Token);
            var reply = new byte[8];
            await stream.ReadExactlyAsync(reply, timeout.Token);
            Assert.Equal((ushort)CtrlMessageType.HeartbeatReply, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(4)));

            var sessionPayload = consoleCipher.Encrypt(consoleCounter, Encoding.ASCII.GetBytes("J" + ValidSessionId));
            await stream.WriteAsync(Frame(CtrlMessageType.SessionId, sessionPayload), timeout.Token);

            var enable = new byte[8 + 3];
            await stream.ReadExactlyAsync(enable, timeout.Token);
            Assert.Equal((ushort)CtrlMessageType.EnableDualSenseFeatures, BinaryPrimitives.ReadUInt16BigEndian(enable.AsSpan(4)));
            Assert.Equal(new byte[] { 0x00, 0x40, 0x00 }, consoleCipher.Decrypt(5, enable.AsSpan(8)));

            var dualSenseConnect = new byte[8 + 16];
            await stream.ReadExactlyAsync(dualSenseConnect, timeout.Token);
            Assert.Equal((ushort)CtrlMessageType.DualSenseConnect, BinaryPrimitives.ReadUInt16BigEndian(dualSenseConnect.AsSpan(4)));

            var displayDevices = new byte[8 + 4];
            await stream.ReadExactlyAsync(displayDevices, timeout.Token);
            Assert.Equal((ushort)CtrlMessageType.DisplayDevices, BinaryPrimitives.ReadUInt16BigEndian(displayDevices.AsSpan(4)));
        });

        await using var ctrl = await CtrlChannel.ConnectAsync(endPoint, RegistKey, clientCipher, secrets, VideoCodec.Hevc, timeout.Token);
        Assert.Equal(2, ctrl.ServerType);

        var sessionId = await ctrl.WaitForSessionIdAsync(null, TimeSpan.FromSeconds(5), timeout.Token);
        Assert.Equal(ValidSessionId, sessionId);

        await ctrl.EnableFeaturesAsync(dualSense: true, timeout.Token);
        await console;
        listener.Stop();
    }

    private static byte[] Frame(CtrlMessageType type, byte[] encryptedPayload)
    {
        var frame = new byte[8 + encryptedPayload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)encryptedPayload.Length);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)type);
        encryptedPayload.CopyTo(frame, 8);
        return frame;
    }

    private static TcpListener Listen(out IPEndPoint endPoint)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        endPoint = (IPEndPoint)listener.LocalEndpoint;
        return listener;
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var received = new MemoryStream();
        var one = new byte[1];
        while (RpHttpResponse.FindHeaderEnd(received.ToArray()) < 0)
        {
            await stream.ReadExactlyAsync(one, cancellationToken);
            received.Write(one);
        }

        return Encoding.ASCII.GetString(received.ToArray());
    }

    private static byte[] Pad16(string text)
    {
        var bytes = new byte[16];
        Encoding.ASCII.GetBytes(text, bytes);
        return bytes;
    }
}
