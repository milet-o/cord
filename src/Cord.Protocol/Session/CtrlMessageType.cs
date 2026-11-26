namespace Cord.Protocol.Session;

/// <summary>Ctrl channel message types (docs/protocol/06-ctrl.md).</summary>
public enum CtrlMessageType : ushort
{
    DisplayA = 0x0001,
    LoginPinRequest = 0x0004,
    LoginResult = 0x0005,
    DualSenseConnect = 0x0011,
    EnableDualSenseFeatures = 0x0013,
    GoHome = 0x0014,
    DisplayB = 0x0016,
    SessionId = 0x0033,
    SwitchToStreamConnection = 0x0034,
    GoToRest = 0x0050,
    HeartbeatRequest = 0x00FE,
    HeartbeatReply = 0x01FE,
    DisplayDevices = 0x0910,
    LoginPinReply = 0x8004,
}
