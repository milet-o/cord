using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cord.Protocol.Crypto;
using Cord.Protocol.Http;

namespace Cord.Protocol.Session;

/// <summary>
/// Provides the console login passcode. <c>previousAttemptFailed</c> is true when the console
/// rejected the last passcode. Returning null aborts the connection.
/// </summary>
public delegate Task<string?> LoginPasscodeProvider(bool previousAttemptFailed, CancellationToken cancellationToken);

/// <summary>The long-lived ctrl TCP connection (docs/protocol/06-ctrl.md).</summary>
public sealed class CtrlChannel : IAsyncDisposable
{
    public const int DefaultPort = 9295;

    private const int HeaderSize = 8;
    private const int MaxPayloadSize = 64 * 1024;
    private const byte SessionIdMarker = 0x4A;
    private const int MinSessionIdLength = 24;
    private const int MaxSessionIdLength = 78;
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);
    private static readonly byte[] OsType = "Win10.0.0\0"u8.ToArray();

    private readonly RpHttpConnection _connection;
    private readonly RpCipher _cipher;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource<string> _sessionId = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private ulong _localCounter;
    private ulong _remoteCounter;
    private Task? _readLoop;
    private LoginPasscodeProvider? _passcodeProvider;

    private CtrlChannel(RpHttpConnection connection, RpCipher cipher, ulong localCounter)
    {
        _connection = connection;
        _cipher = cipher;
        _localCounter = localCounter;
    }

    /// <summary>Console type from RP-Server-Type: 0 PS4, 1 PS4 Pro, 2 PS5.</summary>
    public int? ServerType { get; private set; }

    public bool StreamingProhibited { get; private set; }

    public bool? DisplayBlocked { get; private set; }

    /// <summary>Completes when the console closes the channel or it fails.</summary>
    public Task Closed => _closed.Task;

    public static async Task<CtrlChannel> ConnectAsync(
        IPEndPoint console,
        ReadOnlyMemory<byte> registKey,
        RpCipher sessionCipher,
        SessionSecrets secrets,
        VideoCodec codec,
        CancellationToken cancellationToken = default)
    {
        var (request, localCounter) = BuildRequest(console, registKey.Span, sessionCipher, secrets, codec);

        for (var attempt = 0; ; attempt++)
        {
            var connection = await RpHttpConnection.ConnectAsync(console, cancellationToken);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ResponseTimeout);
                await connection.SendAsync(request, ReadOnlyMemory<byte>.Empty, timeout.Token);
                var response = await connection.ReadResponseAsync(timeout.Token);
                if (response.StatusCode != 200)
                    throw RemotePlayException.FromApplicationReason(response.ApplicationReason, "Ctrl connection");

                var channel = new CtrlChannel(connection, sessionCipher, localCounter);
                channel.ApplyResponse(response);
                return channel;
            }
            catch (OperationCanceledException) when (attempt == 0 && !cancellationToken.IsCancellationRequested)
            {
                await connection.DisposeAsync();
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
    }

    /// <summary>
    /// Starts processing console messages and waits for the session id, answering login
    /// passcode requests through <paramref name="passcodeProvider"/>.
    /// </summary>
    public async Task<string> WaitForSessionIdAsync(
        LoginPasscodeProvider? passcodeProvider,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        _passcodeProvider = passcodeProvider;
        _readLoop ??= Task.Run(() => ReadLoopAsync(_stop.Token), CancellationToken.None);

        Task completed;
        try
        {
            completed = await Task.WhenAny(_sessionId.Task, _closed.Task).WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return FallbackSessionId();
        }

        if (completed == _sessionId.Task)
            return await _sessionId.Task;
        await _closed.Task;
        throw new RemotePlayException(RemotePlayError.Disconnected, "The console closed the ctrl channel.");
    }

    public Task SendAsync(CtrlMessageType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
        SendMessageAsync((ushort)type, payload, cancellationToken);

    public Task GoToRestAsync(CancellationToken cancellationToken = default) =>
        SendAsync(CtrlMessageType.GoToRest, ReadOnlyMemory<byte>.Empty, cancellationToken);

    public Task GoHomeAsync(CancellationToken cancellationToken = default)
    {
        var payload = new byte[16];
        payload[1] = 0xFF;
        return SendAsync(CtrlMessageType.GoHome, payload, cancellationToken);
    }

    public async Task EnableFeaturesAsync(bool dualSense, CancellationToken cancellationToken = default)
    {
        if (dualSense)
        {
            await SendAsync(CtrlMessageType.EnableDualSenseFeatures, new byte[] { 0x00, 0x40, 0x00 }, cancellationToken);
            await SendAsync(
                CtrlMessageType.DualSenseConnect,
                new byte[] { 0xA0, 0xAB, 0x51, 0xBD, 0xD1, 0x7E, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00 },
                cancellationToken);
        }

        await SendAsync(CtrlMessageType.DisplayDevices, new byte[4], cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _connection.DisposeAsync();
        if (_readLoop is not null)
        {
            try
            {
                await _readLoop;
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }

        _stop.Dispose();
        _sendLock.Dispose();
    }

    internal static (string Request, ulong NextLocalCounter) BuildRequest(
        IPEndPoint console,
        ReadOnlySpan<byte> registKey,
        RpCipher cipher,
        SessionSecrets secrets,
        VideoCodec codec)
    {
        var auth = new byte[RpCipher.KeySize];
        registKey[..Math.Min(registKey.Length, auth.Length)].CopyTo(auth);

        var streamingType = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(streamingType, (uint)codec);

        string Encrypt(ulong counter, ReadOnlySpan<byte> value) => Convert.ToBase64String(cipher.Encrypt(counter, value));

        var request =
            "GET /sie/ps5/rp/sess/ctrl HTTP/1.1\r\n"
            + $"Host: {console.Address}:{console.Port.ToString(CultureInfo.InvariantCulture)}\r\n"
            + "User-Agent: remoteplay Windows\r\n"
            + "Connection: keep-alive\r\n"
            + "Content-Length: 0\r\n"
            + $"RP-Auth: {Encrypt(0, auth)}\r\n"
            + "RP-Version: 1.0\r\n"
            + $"RP-Did: {Encrypt(1, secrets.DeviceId)}\r\n"
            + "RP-ControllerType: 3\r\n"
            + "RP-ClientType: 11\r\n"
            + $"RP-OSType: {Encrypt(2, OsType)}\r\n"
            + "RP-ConPath: 1\r\n"
            + "RP-PadProcNo: 3\r\n"
            + $"RP-StartBitrate: {Encrypt(3, new byte[4])}\r\n"
            + $"RP-StreamingType: {Encrypt(4, streamingType)}\r\n"
            + "\r\n";
        return (request, 5);
    }

    internal static string? ParseSessionId(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[0] != SessionIdMarker)
            return null;
        var id = payload[1..];
        if (id.Length is < MinSessionIdLength or > MaxSessionIdLength)
            return null;
        foreach (var c in id)
        {
            if (!char.IsAsciiLetterOrDigit((char)c))
                return null;
        }

        return Encoding.ASCII.GetString(id);
    }

    private void ApplyResponse(RpHttpResponse response)
    {
        StreamingProhibited = response.Header("RP-Prohibit") == "1";

        var serverType = response.Header("RP-Server-Type");
        if (serverType is not null)
        {
            var encrypted = Convert.FromBase64String(serverType);
            ServerType = _cipher.Decrypt(_remoteCounter++, encrypted)[0];
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stream = _connection.Stream;
            var buffer = new List<byte>(_connection.TakePending());
            var chunk = new byte[4096];

            while (!cancellationToken.IsCancellationRequested)
            {
                while (TryTakeMessage(buffer, out var type, out var payload))
                    await HandleMessageAsync(type, payload, cancellationToken);

                var read = await stream.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                    break;
                buffer.AddRange(chunk.AsSpan(0, read));
            }

            _closed.TrySetResult();
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            _closed.TrySetResult();
        }
        catch (Exception e)
        {
            _closed.TrySetException(e);
        }
    }

    private bool TryTakeMessage(List<byte> buffer, out ushort type, out byte[] payload)
    {
        type = 0;
        payload = [];
        if (buffer.Count < HeaderSize)
            return false;

        Span<byte> header = stackalloc byte[HeaderSize];
        for (var i = 0; i < HeaderSize; i++)
            header[i] = buffer[i];
        var size = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (size > MaxPayloadSize)
            throw new InvalidDataException("Ctrl message is too large.");
        if (buffer.Count < HeaderSize + size)
            return false;

        type = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        var encrypted = buffer.GetRange(HeaderSize, (int)size).ToArray();
        buffer.RemoveRange(0, HeaderSize + (int)size);
        payload = encrypted.Length == 0 ? encrypted : _cipher.Decrypt(_remoteCounter++, encrypted);
        return true;
    }

    private async Task HandleMessageAsync(ushort type, byte[] payload, CancellationToken cancellationToken)
    {
        switch ((CtrlMessageType)type)
        {
            case CtrlMessageType.SessionId:
                if (!_sessionId.Task.IsCompleted)
                    _sessionId.TrySetResult(ParseSessionId(payload) ?? FallbackSessionId());
                break;
            case CtrlMessageType.HeartbeatRequest:
                await SendAsync(CtrlMessageType.HeartbeatReply, ReadOnlyMemory<byte>.Empty, cancellationToken);
                break;
            case CtrlMessageType.LoginPinRequest:
                await AnswerLoginAsync(previousAttemptFailed: false, cancellationToken);
                break;
            case CtrlMessageType.LoginResult when payload.Length > 0 && payload[0] != 0:
                await AnswerLoginAsync(previousAttemptFailed: true, cancellationToken);
                break;
            case CtrlMessageType.DisplayA when payload.Length > 0:
                DisplayBlocked = payload[0] == 1;
                break;
            case CtrlMessageType.DisplayB when payload.Length > 1:
                DisplayBlocked = payload[0] == 0x01 && payload[1] == 0xFF;
                break;
        }
    }

    private async Task AnswerLoginAsync(bool previousAttemptFailed, CancellationToken cancellationToken)
    {
        var passcode = _passcodeProvider is null ? null : await _passcodeProvider(previousAttemptFailed, cancellationToken);
        if (passcode is null)
        {
            _sessionId.TrySetException(new RemotePlayException(RemotePlayError.LoginFailed, "The console requires a login passcode."));
            return;
        }

        await SendAsync(CtrlMessageType.LoginPinReply, Encoding.ASCII.GetBytes(passcode), cancellationToken);
    }

    private async Task SendMessageAsync(ushort type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            var message = new byte[HeaderSize + payload.Length];
            BinaryPrimitives.WriteUInt32BigEndian(message, (uint)payload.Length);
            BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), type);
            if (!payload.IsEmpty)
                _cipher.Encrypt(_localCounter++, payload.Span).CopyTo(message, HeaderSize);
            await _connection.Stream.WriteAsync(message, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // The console accepts a locally generated id when it never sends one.
    private static string FallbackSessionId()
    {
        var seconds = (Environment.TickCount64 / 1000).ToString(CultureInfo.InvariantCulture);
        var random = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        return seconds + random;
    }
}
