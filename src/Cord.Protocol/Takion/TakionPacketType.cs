namespace Cord.Protocol.Takion;

/// <summary>Low nibble of the first datagram byte (docs/protocol/07-takion.md).</summary>
public enum TakionPacketType : byte
{
    Control = 0,
    FeedbackHistory = 1,
    Video = 2,
    Audio = 3,
    Handshake = 4,
    Congestion = 5,
    FeedbackState = 6,
    ClientInfo = 8,
}

public enum TakionChunkType : byte
{
    Data = 0x00,
    Init = 0x01,
    InitAck = 0x02,
    DataAck = 0x03,
    Cookie = 0x0A,
    CookieAck = 0x0B,
}

/// <summary>Data types carried by DATA chunks.</summary>
public enum TakionDataType : byte
{
    Protobuf = 0,
    Rumble = 7,
    PadInfo = 9,
    TriggerEffects = 11,
}
