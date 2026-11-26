using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Cord.Protocol.Crypto;

namespace Cord.Protocol.Takion;

/// <summary>
/// The Takion UDP transport: handshake, reliable DATA chunks with acknowledgement and
/// retransmission, and authentication of all packets once the stream cipher is set
/// (docs/protocol/07-takion.md).
/// </summary>
public sealed class TakionConnection : IAsyncDisposable
{
    public const uint ReceiveWindow = 0x19000;
    public const ushort StreamCount = 0x64;
    public const ushort MessageChannel = 1;

    private const int MaxDatagramSize = 2048;
    private const int HandshakeAttempts = 3;
    private const int ReorderWindow = 16;
    private const int PostponedPacketLimit = 32;
    private const int MaxRetransmissions = 10;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetransmitInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan GapTimeout = TimeSpan.FromMilliseconds(500);

    private readonly Socket _socket;
    private readonly ITakionHandler _handler;
    private readonly uint _localTag;
    private readonly bool _encrypted;
    private readonly Lock _cryptLock = new();
    private readonly Lock _sendBufferLock = new();
    private readonly Dictionary<uint, PendingData> _unacknowledged = [];
    private readonly SortedDictionary<uint, byte[]> _reorder = [];
    private readonly List<byte[]> _postponed = [];
    private readonly KeyPosTracker _remoteKeyPos = new();
    private readonly CancellationTokenSource _stop = new();
    private GkCrypt? _localCrypt;
    private GkCrypt? _remoteCrypt;
    private ulong _localKeyPos;
    private uint _remoteTag;
    private uint _nextLocalSequence;
    private uint _nextRemoteSequence;
    private long _gapSince;
    private long _lastReceived;
    private int _closeReported;
    private Task? _receiveLoop;
    private Task? _retransmitLoop;

    private TakionConnection(Socket socket, ITakionHandler handler, bool encrypted)
    {
        _socket = socket;
        _handler = handler;
        _encrypted = encrypted;
        _localTag = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        _nextLocalSequence = _localTag;
    }

    public static Task<TakionConnection> ConnectAsync(
        IPEndPoint console,
        ITakionHandler handler,
        bool dontFragment,
        CancellationToken cancellationToken = default) =>
        ConnectAsync(console, handler, dontFragment, encrypted: true, cancellationToken);

    /// <summary>
    /// Connects without expecting a stream cipher when <paramref name="encrypted"/> is false
    /// (senkusha): AV packets are then delivered unauthenticated.
    /// </summary>
    public static async Task<TakionConnection> ConnectAsync(
        IPEndPoint console,
        ITakionHandler handler,
        bool dontFragment,
        bool encrypted,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(console.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 4 * 1024 * 1024,
            SendBufferSize = 1024 * 1024,
        };
        TakionConnection? connection = null;
        try
        {
            if (console.AddressFamily == AddressFamily.InterNetwork)
            {
                if (dontFragment)
                    socket.DontFragment = true;
                // Mark EF (DSCP 46 / TOS 0xB8) when permitted by OS policy for lower Wi-Fi/router queuing jitter.
                try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0xB8); }
                catch (SocketException) { }
            }
            await socket.ConnectAsync(console, cancellationToken);
            connection = new TakionConnection(socket, handler, encrypted);
            await connection.HandshakeAsync(cancellationToken);
            connection.Start();
            return connection;
        }
        catch
        {
            if (connection is not null)
                await connection.DisposeAsync();
            else
                socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// When set, the connection is reported closed with a <see cref="TimeoutException"/> once
    /// nothing has been received for this long. UDP gives no error when the console or the
    /// network disappears, so silence is the only sign of a lost connection.
    /// </summary>
    public TimeSpan? SilenceTimeout { get; set; }

    /// <summary>Enables authentication and encryption, then processes packets that arrived early.</summary>
    public void SetCrypt(GkCrypt local, GkCrypt remote)
    {
        byte[][] postponed;
        lock (_cryptLock)
        {
            _localCrypt = local;
            _remoteCrypt = remote;
            postponed = [.. _postponed];
            _postponed.Clear();
        }

        foreach (var packet in postponed)
            HandleDatagram(packet);
    }

    /// <summary>Sends a reliable message, fragmenting it so no datagram exceeds <paramref name="maxChunkData"/>.</summary>
    public async Task SendMessageAsync(TakionDataType type, ReadOnlyMemory<byte> data, int maxChunkData = int.MaxValue, CancellationToken cancellationToken = default)
    {
        var offset = 0;
        var first = true;
        do
        {
            var limit = first ? maxChunkData : maxChunkData + 1;
            var length = Math.Min(limit, data.Length - offset);
            var last = offset + length >= data.Length;
            await SendDataChunkAsync(first ? type : null, data.Slice(offset, length), last, cancellationToken);
            offset += length;
            first = false;
        }
        while (offset < data.Length);
    }

    /// <summary>Sends an unreliable feedback packet (type 1 or 6), encrypting its payload.</summary>
    public async Task SendFeedbackAsync(TakionPacketType type, ushort sequenceNumber, byte countMinusOne, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var packet = new byte[TakionPackets.FeedbackHeaderSize + payload.Length];
        packet[0] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), sequenceNumber);
        packet[3] = countMinusOne;
        payload.Span.CopyTo(packet.AsSpan(TakionPackets.FeedbackHeaderSize));

        lock (_cryptLock)
        {
            var crypt = _localCrypt ?? throw new InvalidOperationException("Feedback requires the stream cipher.");
            var keyPos = ReserveKeyPos(payload.Length + GkCrypt.BlockSize);
            crypt.Transform(keyPos + GkCrypt.BlockSize, packet.AsSpan(TakionPackets.FeedbackHeaderSize));
            TakionPackets.Sign(crypt, packet, keyPos);
        }

        await _socket.SendAsync(packet, SocketFlags.None, cancellationToken);
    }

    public async Task SendCongestionAsync(ushort received, ushort lost, CancellationToken cancellationToken = default)
    {
        var packet = new byte[TakionPackets.CongestionPacketSize];
        packet[0] = (byte)TakionPacketType.Congestion;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3), received);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(5), lost);
        SignControlLike(packet, TakionPackets.CongestionPacketSize);
        await _socket.SendAsync(packet, SocketFlags.None, cancellationToken);
    }

    /// <summary>Sends a raw datagram as is (senkusha probes).</summary>
    public ValueTask<int> SendRawAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default) =>
        _socket.SendAsync(datagram, SocketFlags.None, cancellationToken);

    /// <summary>
    /// Waits briefly for reliable DATA packets (such as CONTROLLERCONNECTION and DISCONNECT) to be
    /// acknowledged by the console before tearing down the UDP transport.
    /// </summary>
    public async Task FlushUnacknowledgedAsync(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!_stop.IsCancellationRequested && Environment.TickCount64 < deadline)
        {
            lock (_sendBufferLock)
            {
                if (_unacknowledged.Count == 0)
                    return;
            }

            try
            {
                await Task.Delay(15, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _socket.Dispose();
        foreach (var task in new[] { _receiveLoop, _retransmitLoop })
        {
            if (task is null)
                continue;
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        var init = TakionPackets.BuildControl(0, TakionChunkType.Init, 0,
            TakionPackets.BuildInit(new InitParameters(_localTag, ReceiveWindow, StreamCount, StreamCount, _nextLocalSequence)));

        var initAck = await ExchangeAsync(init, TakionChunkType.InitAck, cancellationToken);
        if (initAck.Length != TakionPackets.InitPayloadSize + TakionPackets.CookieSize)
            throw new RemotePlayException(RemotePlayError.InvalidResponse, "Takion INIT_ACK has an invalid size.");
        var parameters = TakionPackets.ReadInit(initAck);
        if (parameters.Tag == 0
            || parameters.OutboundStreams == 0 || parameters.InboundStreams == 0
            || parameters.OutboundStreams > StreamCount || parameters.InboundStreams < StreamCount)
        {
            throw new RemotePlayException(RemotePlayError.InvalidResponse, "Takion INIT_ACK parameters are invalid.");
        }

        _remoteTag = parameters.Tag;
        _nextRemoteSequence = parameters.Tag;

        var cookie = TakionPackets.BuildControl(_remoteTag, TakionChunkType.Cookie, 0, initAck.AsSpan(TakionPackets.InitPayloadSize));
        await ExchangeAsync(cookie, TakionChunkType.CookieAck, cancellationToken);
    }

    private async Task<byte[]> ExchangeAsync(byte[] request, TakionChunkType expected, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxDatagramSize];
        for (var attempt = 0; attempt < HandshakeAttempts; attempt++)
        {
            await _socket.SendAsync(request, SocketFlags.None, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HandshakeTimeout);
            try
            {
                while (true)
                {
                    var received = await _socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);
                    if (TakionPackets.TryParseControl(buffer.AsSpan(0, received), out var chunk) && chunk.Type == expected)
                        return chunk.Payload.ToArray();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                throw new RemotePlayException(RemotePlayError.ConsoleNotFound, "The console refused the Takion connection.");
            }
        }

        throw new RemotePlayException(RemotePlayError.ConsoleNotFound, $"No Takion {expected} from the console.");
    }

    private void Start()
    {
        Volatile.Write(ref _lastReceived, Environment.TickCount64);
        _receiveLoop = Task.Factory.StartNew(ReceiveLoop, TaskCreationOptions.LongRunning);
        _retransmitLoop = Task.Run(() => RetransmitLoopAsync(_stop.Token));
    }

    private void ReceiveLoop()
    {
        try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; }
        catch (Exception) { }

        var buffer = new byte[MaxDatagramSize];
        Exception? error = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                int received;
                try
                {
                    received = _socket.Receive(buffer);
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue;
                }

                if (received > 0)
                {
                    Volatile.Write(ref _lastReceived, Environment.TickCount64);
                    HandleDatagram(buffer.AsSpan(0, received));
                }
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or SocketException && _stop.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            error = e;
        }

        ReportClosed(error);
    }

    /// <summary>Notifies the handler once, whichever of the receive loop or the silence check ends first.</summary>
    private void ReportClosed(Exception? error)
    {
        if (Interlocked.Exchange(ref _closeReported, 1) == 0)
            _handler.OnClosed(error);
    }

    private void CheckSilence()
    {
        if (SilenceTimeout is not { } timeout)
            return;
        var silentMs = Environment.TickCount64 - Volatile.Read(ref _lastReceived);
        if (silentMs >= timeout.TotalMilliseconds)
            ReportClosed(new TimeoutException($"Nothing received from the console for {silentMs / 1000.0:0.#} s."));
    }

    private void HandleDatagram(Span<byte> datagram)
    {
        var type = TakionPackets.TypeOf(datagram);
        if (type is not (TakionPacketType.Control or TakionPacketType.Video or TakionPacketType.Audio))
            return;

        ulong keyPos = 0;
        lock (_cryptLock)
        {
            if (_remoteCrypt is null)
            {
                if (type != TakionPacketType.Control && _encrypted)
                {
                    if (_postponed.Count < PostponedPacketLimit)
                        _postponed.Add(datagram.ToArray());
                    return;
                }
            }
            else
            {
                if (datagram.Length < 0x12)
                    return;
                keyPos = _remoteKeyPos.Resolve(TakionPackets.ReadKeyPosLow(datagram));
                if (!TakionPackets.Verify(_remoteCrypt, datagram, keyPos))
                    return;
                _remoteKeyPos.Commit(keyPos);
            }
        }

        if (type == TakionPacketType.Control)
            HandleControl(datagram);
        else
            _handler.OnAvPacket(datagram, keyPos);
    }

    private void HandleControl(ReadOnlySpan<byte> datagram)
    {
        if (!TakionPackets.TryParseControl(datagram, out var chunk))
            return;

        switch (chunk.Type)
        {
            case TakionChunkType.Data when chunk.Payload.Length >= TakionPackets.DataHeaderSize:
                ReceiveData(chunk.Payload);
                break;
            case TakionChunkType.DataAck when chunk.Payload.Length >= TakionPackets.DataAckPayloadSize:
                Acknowledge(BinaryPrimitives.ReadUInt32BigEndian(chunk.Payload));
                break;
        }
    }

    private void ReceiveData(ReadOnlySpan<byte> payload)
    {
        var sequence = BinaryPrimitives.ReadUInt32BigEndian(payload);
        var distance = unchecked((int)(sequence - _nextRemoteSequence));

        if (distance < 0)
        {
            SendAck(unchecked(_nextRemoteSequence - 1));
            return;
        }

        if (distance < ReorderWindow)
            _reorder.TryAdd(sequence, payload.ToArray());
        else
            ResyncTo(sequence, payload);

        DeliverInOrder();
    }

    private void ResyncTo(uint sequence, ReadOnlySpan<byte> payload)
    {
        _reorder.Clear();
        _nextRemoteSequence = sequence;
        _reorder[sequence] = payload.ToArray();
    }

    private void DeliverInOrder()
    {
        var delivered = false;
        while (_reorder.Remove(_nextRemoteSequence, out var payload))
        {
            _handler.OnData((TakionDataType)payload[8], payload.AsSpan(TakionPackets.DataHeaderSize));
            _nextRemoteSequence++;
            delivered = true;
        }

        if (delivered)
        {
            _gapSince = 0;
            SendAck(unchecked(_nextRemoteSequence - 1));
        }
        else if (_reorder.Count > 0)
        {
            var now = Environment.TickCount64;
            if (_gapSince == 0)
                _gapSince = now;
            else if (now - _gapSince > GapTimeout.TotalMilliseconds)
            {
                _nextRemoteSequence = _reorder.Keys.First();
                _gapSince = 0;
                DeliverInOrder();
            }
        }
    }

    private void SendAck(uint sequence)
    {
        var packet = TakionPackets.BuildControl(_remoteTag, TakionChunkType.DataAck, 0, TakionPackets.BuildDataAck(sequence, ReceiveWindow));
        SignControlLike(packet, packet.Length);
        try
        {
            _socket.Send(packet);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task SendDataChunkAsync(TakionDataType? type, ReadOnlyMemory<byte> data, bool last, CancellationToken cancellationToken)
    {
        byte[] packet;
        lock (_sendBufferLock)
        {
            var sequence = _nextLocalSequence++;
            packet = TakionPackets.BuildControl(_remoteTag, TakionChunkType.Data, last ? (byte)1 : (byte)0,
                TakionPackets.BuildData(sequence, MessageChannel, type, data.Span));
            SignControlLike(packet, data.Length);
            _unacknowledged[sequence] = new PendingData(packet, Environment.TickCount64, 0);
        }

        await _socket.SendAsync(packet, SocketFlags.None, cancellationToken);
    }

    private void Acknowledge(uint cumulative)
    {
        lock (_sendBufferLock)
        {
            foreach (var sequence in _unacknowledged.Keys.Where(s => unchecked((int)(cumulative - s)) >= 0).ToList())
                _unacknowledged.Remove(sequence);
        }
    }

    private async Task RetransmitLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RetransmitInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            CheckSilence();
            List<byte[]> resend = [];
            lock (_sendBufferLock)
            {
                var now = Environment.TickCount64;
                foreach (var (sequence, pending) in _unacknowledged.ToList())
                {
                    if (now - pending.SentAt < RetransmitInterval.TotalMilliseconds)
                        continue;
                    if (pending.Attempts >= MaxRetransmissions)
                    {
                        _unacknowledged.Remove(sequence);
                        continue;
                    }

                    _unacknowledged[sequence] = pending with { SentAt = now, Attempts = pending.Attempts + 1 };
                    resend.Add(pending.Packet);
                }
            }

            foreach (var packet in resend)
                await _socket.SendAsync(packet, SocketFlags.None, cancellationToken);
        }
    }

    private void SignControlLike(byte[] packet, int keyPosSize)
    {
        lock (_cryptLock)
        {
            var keyPos = ReserveKeyPos(keyPosSize);
            var layout = TakionPackets.AuthenticationLayout(TakionPackets.TypeOf(packet))!.Value;
            if (_localCrypt is null)
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(layout.KeyPosOffset), 0);
            else
                TakionPackets.Sign(_localCrypt, packet, keyPos);
        }
    }

    // Matches the console's accounting: the size is padded by its own remainder modulo 16.
    private ulong ReserveKeyPos(int size)
    {
        if (_localCrypt is null)
            return 0;
        var current = _localKeyPos;
        _localKeyPos += (ulong)(size + (size % GkCrypt.BlockSize));
        return current;
    }

    private readonly record struct PendingData(byte[] Packet, long SentAt, int Attempts);
}
