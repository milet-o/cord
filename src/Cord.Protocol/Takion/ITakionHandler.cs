namespace Cord.Protocol.Takion;

/// <summary>Receives what the console sends on a Takion connection. Called from the receive thread.</summary>
public interface ITakionHandler
{
    void OnData(TakionDataType type, ReadOnlySpan<byte> data);

    /// <summary>A video or audio datagram whose GMAC was verified. The payload is still encrypted.</summary>
    void OnAvPacket(ReadOnlySpan<byte> datagram, ulong keyPos);

    void OnClosed(Exception? exception);
}
