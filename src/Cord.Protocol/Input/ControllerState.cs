namespace Cord.Protocol.Input;

[Flags]
public enum ControllerButtons : uint
{
    None = 0,
    Cross = 1 << 0,
    Circle = 1 << 1,
    Square = 1 << 2,
    Triangle = 1 << 3,
    DpadLeft = 1 << 4,
    DpadRight = 1 << 5,
    DpadUp = 1 << 6,
    DpadDown = 1 << 7,
    L1 = 1 << 8,
    R1 = 1 << 9,
    L3 = 1 << 10,
    R3 = 1 << 11,
    Options = 1 << 12,
    Create = 1 << 13,
    Touchpad = 1 << 14,
    PS = 1 << 15,
}

/// <summary>A finger on the touchpad. <see cref="Id"/> is -1 when no finger is down.</summary>
public readonly record struct TouchPoint(sbyte Id, ushort X, ushort Y)
{
    public static readonly TouchPoint None = new(-1, 0, 0);
}

/// <summary>Full controller state sent to the console (docs/protocol/13-input.md).</summary>
public readonly record struct ControllerState(
    ControllerButtons Buttons,
    byte L2,
    byte R2,
    short LeftX,
    short LeftY,
    short RightX,
    short RightY,
    TouchPoint Touch0,
    TouchPoint Touch1,
    MotionState Motion)
{
    public static readonly ControllerState Idle = new(
        ControllerButtons.None, 0, 0, 0, 0, 0, 0, TouchPoint.None, TouchPoint.None, MotionState.Neutral);
}

public readonly record struct MotionState(
    float GyroX,
    float GyroY,
    float GyroZ,
    float AccelX,
    float AccelY,
    float AccelZ,
    float OrientX,
    float OrientY,
    float OrientZ,
    float OrientW)
{
    public static readonly MotionState Neutral = new(0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
}
