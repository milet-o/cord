namespace Cord.Protocol.Av;

/// <summary>Callbacks of <see cref="VideoReceiver"/>. Called from the receive thread.</summary>
public interface IVideoReceiverSink
{
    /// <summary>A complete Annex B access unit; <paramref name="streamHeader"/> is set when it must be fed first.</summary>
    void OnFrame(ReadOnlyMemory<byte> frame, ReadOnlyMemory<byte>? streamHeader, bool recoveredWithFec);

    /// <summary>Frames <paramref name="first"/>..<paramref name="last"/> were lost or broken.</summary>
    void OnCorruptFrames(ushort first, ushort last);
}

public sealed record VideoStreamProfile(int Width, int Height, byte[] Header);

/// <summary>Orders video units into frames (docs/protocol/11-video.md#frame-sequencing).</summary>
public sealed class VideoReceiver
{
    private const int MaxDroppedWaitFrames = 90;

    private readonly IVideoReceiverSink _sink;
    private readonly IReadOnlyList<VideoStreamProfile> _profiles;
    private readonly FrameAssembler _assembler = new();
    private readonly bool _dropCorruptUntilKeyframe;
    private readonly bool _isHevc;
    private int _currentProfile = -1;
    private bool _headerPending;
    private int _currentFrame = -1;
    private ushort _lastComplete;
    private ushort _lastHandled;
    private bool _flushedCurrent;
    private bool _awaitingCleanKeyframe;
    private int _droppedWhileWaiting;

    public VideoReceiver(
        IReadOnlyList<VideoStreamProfile> profiles,
        IVideoReceiverSink sink,
        bool dropCorruptUntilKeyframe = false,
        bool isHevc = false)
    {
        _profiles = profiles;
        _sink = sink;
        _dropCorruptUntilKeyframe = dropCorruptUntilKeyframe;
        _isHevc = isHevc;
    }

    public VideoStreamProfile? CurrentProfile => _currentProfile >= 0 ? _profiles[_currentProfile] : null;

    public int FramesLost { get; private set; }

    public int FramesDroppedForRecovery { get; private set; }

    public void OnPacket(AvPacket packet, ReadOnlySpan<byte> unitData)
    {
        var frame = packet.FrameIndex;
        if (_currentFrame >= 0 && IsBefore(frame, (ushort)_currentFrame))
            return;

        if (packet.AdaptiveStreamIndex != _currentProfile)
        {
            if (packet.AdaptiveStreamIndex >= _profiles.Count)
                return;
            _currentProfile = packet.AdaptiveStreamIndex;
            _headerPending = true;
        }

        if (_currentFrame < 0 || IsBefore((ushort)_currentFrame, frame))
            StartFrame(frame, packet, unitData);

        _assembler.Add(packet, unitData);

        if (!_flushedCurrent && (_assembler.HasAllSourceUnits || packet.UnitIndex == packet.UnitsTotal - 1))
            FlushCurrent();
    }

    private void StartFrame(ushort frame, AvPacket packet, ReadOnlySpan<byte> unitData)
    {
        if (_currentFrame >= 0 && !_flushedCurrent)
            FlushCurrent();

        var expected = (ushort)(_lastHandled + 1);
        var firstFrame = _currentFrame < 0;
        if (!firstFrame && IsBefore(expected, frame))
        {
            FramesLost += (ushort)(frame - expected);
            _sink.OnCorruptFrames(expected, (ushort)(frame - 1));
            _lastHandled = (ushort)(frame - 1);
            if (_dropCorruptUntilKeyframe)
                _awaitingCleanKeyframe = true;
        }

        _currentFrame = frame;
        _flushedCurrent = false;
        _assembler.Begin(frame, packet, unitData);
        if (firstFrame)
            _lastComplete = _lastHandled = (ushort)(frame - 1);
    }

    private void FlushCurrent()
    {
        _flushedCurrent = true;
        var firstUnreported = (ushort)(_lastHandled + 1);
        _lastHandled = (ushort)_currentFrame;
        var result = _assembler.Flush(out var data);
        if (result == FrameResult.Incomplete)
        {
            FramesLost++;
            _sink.OnCorruptFrames(firstUnreported, (ushort)_currentFrame);
            if (_dropCorruptUntilKeyframe)
                _awaitingCleanKeyframe = true;
            return;
        }

        if (_awaitingCleanKeyframe)
        {
            var isKey = ContainsKeyframeNal(data.AsSpan(), _isHevc);
            if (!isKey && _droppedWhileWaiting < MaxDroppedWaitFrames)
            {
                _droppedWhileWaiting++;
                FramesDroppedForRecovery++;
                return;
            }

            _awaitingCleanKeyframe = false;
            _droppedWhileWaiting = 0;
            _headerPending = true;
        }

        ReadOnlyMemory<byte>? header = null;
        if (_headerPending)
        {
            header = _profiles[_currentProfile].Header;
            _headerPending = false;
        }

        _lastComplete = (ushort)_currentFrame;
        _sink.OnFrame(data, header, result == FrameResult.RecoveredWithFec);
    }

    internal static bool ContainsKeyframeNal(ReadOnlySpan<byte> annexB, bool isHevc)
    {
        var i = 0;
        while (i + 3 < annexB.Length)
        {
            int startLen;
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
                startLen = 3;
            else if (i + 4 < annexB.Length && annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1)
                startLen = 4;
            else
            {
                i++;
                continue;
            }

            var headerIdx = i + startLen;
            if (headerIdx >= annexB.Length)
                break;

            if (isHevc)
            {
                var nalType = (annexB[headerIdx] >> 1) & 0x3F;
                if (nalType is 19 or 20 or 21)
                    return true;
            }
            else
            {
                var nalType = annexB[headerIdx] & 0x1F;
                if (nalType == 5)
                    return true;
            }

            i = headerIdx + 1;
        }

        return false;
    }

    private static bool IsBefore(ushort a, ushort b) => unchecked((short)(a - b)) < 0;
}
