using Cord.Protocol.Takion;

namespace Cord.Protocol.Input;

public interface IFeedbackTransport
{
    Task SendFeedbackAsync(TakionPacketType type, ushort sequenceNumber, byte countMinusOne, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
}

/// <summary>
/// Sends controller state and history events with the timing described in
/// docs/protocol/13-input.md#reliability.
/// </summary>
public sealed class FeedbackSender : IAsyncDisposable
{
    private const int HistoryCapacity = 16;
    private const int EventsPerPacket = 8;
    private static readonly TimeSpan StateMinInterval = TimeSpan.FromMilliseconds(4);
    private static readonly TimeSpan StateMaxInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan[] HistoryTail =
        [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)];

    private readonly IFeedbackTransport _transport;
    private readonly TimeProvider _time;
    private readonly TimeSpan _stateMinInterval;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly LinkedList<(ushort Sequence, HistoryEvent Event)> _history = new();
    private readonly byte[] _stateBuffer = new byte[FeedbackEncoding.StateSize];
    private readonly Task _loop;
    private ControllerState _state = ControllerState.Idle;
    private ControllerState _historyState = ControllerState.Idle;
    private bool _stateDirty = true;
    private bool _historyDirty;
    private ushort _nextHistorySequence;
    private ushort _stateSequence;
    private long _lastStateSent;
    private long _lastHistorySent;
    private int _tailResends = HistoryTail.Length;
    private byte[]? _lastHistoryPacket;
    private (ushort Sequence, byte Count) _lastHistoryHeader;

    public FeedbackSender(IFeedbackTransport transport, TimeProvider? time = null, TimeSpan? stateMinInterval = null)
    {
        _transport = transport;
        _time = time ?? TimeProvider.System;
        _stateMinInterval = stateMinInterval is { } interval && interval > TimeSpan.Zero ? interval : StateMinInterval;
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    public void Update(in ControllerState state)
    {
        lock (_lock)
        {
            if (state == _state)
                return;

            foreach (var historyEvent in HistoryRecorder.Diff(_historyState, state))
            {
                _history.AddFirst((_nextHistorySequence++, historyEvent));
                if (_history.Count > HistoryCapacity)
                    _history.RemoveLast();
                _historyDirty = true;
            }

            _historyState = state;
            _stateDirty = true;
            _state = state;
        }

        _wake.Release();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
        _wake.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var wait = await SendDueAsync(cancellationToken);
            await _wake.WaitAsync(wait, cancellationToken);
        }
    }

    /// <summary>Sends whatever is due and returns how long to wait before checking again.</summary>
    private async Task<TimeSpan> SendDueAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetTimestamp();
        byte[]? statePayload = null;
        ushort stateSequence = 0;
        byte[]? historyPayload = null;
        (ushort Sequence, byte Count) historyHeader = default;
        var nextCheck = StateMaxInterval;

        lock (_lock)
        {
            var sinceState = _time.GetElapsedTime(_lastStateSent, now);
            if ((_stateDirty && sinceState >= _stateMinInterval) || sinceState >= StateMaxInterval)
            {
                FeedbackEncoding.WriteState(_state, _stateBuffer);
                statePayload = _stateBuffer;
                stateSequence = _stateSequence++;
                _stateDirty = false;
                _lastStateSent = now;
            }
            else if (_stateDirty)
            {
                nextCheck = _stateMinInterval - sinceState;
            }

            if (_historyDirty)
            {
                var totalBytes = 0;
                var count = 0;
                ushort firstSeq = 0;
                for (var node = _history.First; node is not null && count < EventsPerPacket; node = node.Next)
                {
                    if (count == 0)
                        firstSeq = node.Value.Sequence;
                    totalBytes += node.Value.Event.Bytes.Length;
                    count++;
                }

                historyPayload = new byte[totalBytes];
                var offset = 0;
                var copied = 0;
                for (var node = _history.First; node is not null && copied < count; node = node.Next)
                {
                    var bytes = node.Value.Event.Bytes;
                    bytes.CopyTo(historyPayload.AsSpan(offset));
                    offset += bytes.Length;
                    copied++;
                }

                historyHeader = (firstSeq, (byte)(count - 1));
                _lastHistoryPacket = historyPayload;
                _lastHistoryHeader = historyHeader;
                _lastHistorySent = now;
                _tailResends = 0;
                _historyDirty = false;
            }
            else if (_lastHistoryPacket is not null && _tailResends < HistoryTail.Length)
            {
                var sinceHistory = _time.GetElapsedTime(_lastHistorySent, now);
                if (sinceHistory >= HistoryTail[_tailResends])
                {
                    historyPayload = _lastHistoryPacket;
                    historyHeader = _lastHistoryHeader;
                    _tailResends++;
                }
            }

            if (_lastHistoryPacket is not null && _tailResends < HistoryTail.Length)
            {
                var untilTail = HistoryTail[_tailResends] - _time.GetElapsedTime(_lastHistorySent, now);
                if (untilTail < nextCheck)
                    nextCheck = untilTail;
            }
        }

        if (statePayload is not null)
            await _transport.SendFeedbackAsync(TakionPacketType.FeedbackState, stateSequence, 0, statePayload, cancellationToken);
        if (historyPayload is not null)
            await _transport.SendFeedbackAsync(TakionPacketType.FeedbackHistory, historyHeader.Sequence, historyHeader.Count, historyPayload, cancellationToken);

        return nextCheck < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : nextCheck;
    }
}
