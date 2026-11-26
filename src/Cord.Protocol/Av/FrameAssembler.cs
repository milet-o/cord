using System.Buffers.Binary;

namespace Cord.Protocol.Av;

public enum FrameResult
{
    Complete,
    RecoveredWithFec,
    Incomplete,
}

/// <summary>
/// Collects the units of one video frame and rebuilds it, using FEC when source units are
/// missing (docs/protocol/11-video.md#frame-structure).
/// </summary>
public sealed class FrameAssembler
{
    private const int MaxUnits = 2048;
    private const int PaddingFieldSize = 2;

    private byte[][] _slots = [];
    private int[] _lengths = [];
    private bool[] _present = [];
    private int _sourceUnits;
    private int _fecUnits;
    private int _unitSize;
    private int _sourceReceived;
    private int _totalReceived;

    public ushort FrameIndex { get; private set; }

    public bool Active { get; private set; }

    public bool HasAllSourceUnits => Active && _sourceReceived == _sourceUnits;

    public void Begin(ushort frameIndex, AvPacket packet, ReadOnlySpan<byte> unitData)
    {
        var fec = Math.Max(1, packet.FecUnits);
        var source = packet.UnitsTotal - packet.FecUnits;
        if (source <= 0 || source + fec > MaxUnits)
        {
            Active = false;
            return;
        }

        var unitSize = unitData.Length;
        if (packet.UnitIndex < source && unitData.Length >= PaddingFieldSize)
            unitSize += BinaryPrimitives.ReadUInt16BigEndian(unitData);

        FrameIndex = frameIndex;
        _sourceUnits = source;
        _fecUnits = fec;
        _unitSize = unitSize;
        _sourceReceived = 0;
        _totalReceived = 0;
        var count = source + fec;
        _slots = new byte[count][];
        _lengths = new int[count];
        _present = new bool[count];
        Active = true;
    }

    public void Add(AvPacket packet, ReadOnlySpan<byte> unitData)
    {
        var index = packet.UnitIndex;
        if (!Active || index >= _present.Length || _present[index] || unitData.IsEmpty || unitData.Length > _unitSize)
            return;

        var slot = new byte[_unitSize];
        unitData.CopyTo(slot);
        _slots[index] = slot;
        _lengths[index] = unitData.Length;
        _present[index] = true;
        _totalReceived++;
        if (index < _sourceUnits)
            _sourceReceived++;
    }

    /// <summary>Returns the rebuilt frame (without padding fields) and finishes it.</summary>
    public FrameResult Flush(out byte[] frame)
    {
        frame = [];
        if (!Active)
            return FrameResult.Incomplete;
        Active = false;

        var result = FrameResult.Complete;
        if (_sourceReceived < _sourceUnits)
        {
            if (!TryRecover())
                return FrameResult.Incomplete;
            result = FrameResult.RecoveredWithFec;
        }

        var size = 0;
        for (var i = 0; i < _sourceUnits; i++)
            size += Math.Max(0, _lengths[i] - PaddingFieldSize);

        frame = new byte[size];
        var offset = 0;
        for (var i = 0; i < _sourceUnits; i++)
        {
            var length = _lengths[i] - PaddingFieldSize;
            if (length <= 0)
                continue;
            _slots[i].AsSpan(PaddingFieldSize, length).CopyTo(frame.AsSpan(offset));
            offset += length;
        }

        return result;
    }

    private bool TryRecover()
    {
        var units = new Memory<byte>[_present.Length];
        for (var i = 0; i < units.Length; i++)
            units[i] = _slots[i] ??= new byte[_unitSize];

        if (!CauchyFec.Decode(units, _present, _sourceUnits, _fecUnits))
            return false;

        for (var i = 0; i < _sourceUnits; i++)
        {
            if (_present[i])
                continue;
            var padding = BinaryPrimitives.ReadUInt16BigEndian(_slots[i]);
            if (padding >= _unitSize)
                return false;
            _lengths[i] = _unitSize - padding;
        }

        return true;
    }
}
