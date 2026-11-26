namespace Cord.Protocol.Takion;

/// <summary>Reconstructs 64-bit key positions from the 32 bits sent on the wire.</summary>
public sealed class KeyPosTracker
{
    private ulong _previous;

    public ulong Resolve(uint low)
    {
        var previousLow = (uint)_previous;
        var high = (uint)(_previous >> 32);
        var delta = unchecked((int)(low - previousLow));

        if (delta > 0 && low < previousLow)
            high++;
        else if (delta < 0 && low > previousLow && high > 0)
            high--;

        return ((ulong)high << 32) | low;
    }

    public void Commit(ulong keyPos) => _previous = keyPos;
}
