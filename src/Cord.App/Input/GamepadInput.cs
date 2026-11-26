using System.Runtime.InteropServices;
using Hexa.NET.SDL3;
using Cord.Protocol.Input;
using Cord.Protocol.Stream;

namespace Cord.App.Input;

/// <summary>Reads the first connected gamepad and maps it to the console's controller state.</summary>
internal sealed unsafe class GamepadInput : IDisposable
{
    private const float StandardGravity = 9.80665f;
    private const int TouchpadWidth = 1920;
    private const int TouchpadHeight = 1080;

    private static readonly (SDLGamepadButton Sdl, ControllerButtons Console)[] ButtonMap =
    [
        (SDLGamepadButton.South, ControllerButtons.Cross),
        (SDLGamepadButton.East, ControllerButtons.Circle),
        (SDLGamepadButton.West, ControllerButtons.Square),
        (SDLGamepadButton.North, ControllerButtons.Triangle),
        (SDLGamepadButton.DpadUp, ControllerButtons.DpadUp),
        (SDLGamepadButton.DpadDown, ControllerButtons.DpadDown),
        (SDLGamepadButton.DpadLeft, ControllerButtons.DpadLeft),
        (SDLGamepadButton.DpadRight, ControllerButtons.DpadRight),
        (SDLGamepadButton.LeftShoulder, ControllerButtons.L1),
        (SDLGamepadButton.RightShoulder, ControllerButtons.R1),
        (SDLGamepadButton.LeftStick, ControllerButtons.L3),
        (SDLGamepadButton.RightStick, ControllerButtons.R3),
        (SDLGamepadButton.Start, ControllerButtons.Options),
        (SDLGamepadButton.Back, ControllerButtons.Create),
        (SDLGamepadButton.Guide, ControllerButtons.PS),
        (SDLGamepadButton.Touchpad, ControllerButtons.Touchpad),
    ];

    private SDLGamepadPtr _gamepad;
    private bool _hasMotion;
    private bool _feedbackActive;

    public string? Name { get; private set; }

    public bool IsDualSense { get; private set; }

    public bool Connected => !_gamepad.IsNull;

    public bool IsUsbDualSense => IsDualSense && SDL.GetGamepadConnectionState(_gamepad) == SDLJoystickConnectionState.Wired;

    /// <summary>Opens the first gamepad if none is open. Call on device added/removed events.</summary>
    public void Refresh()
    {
        if (!_gamepad.IsNull && SDL.GamepadConnected(_gamepad))
            return;
        Close();

        int count;
        var ids = SDL.GetGamepads(&count);
        if (ids == null)
            return;
        if (count > 0)
            Open((int)ids[0]);
        SDL.Free(ids);
    }

    public ControllerState Read(ControllerState keyboard)
    {
        if (_gamepad.IsNull)
            return keyboard;

        var buttons = keyboard.Buttons;
        foreach (var (sdl, console) in ButtonMap)
        {
            if (SDL.GetGamepadButton(_gamepad, sdl))
                buttons |= console;
        }

        return keyboard with
        {
            Buttons = buttons,
            L2 = Math.Max(keyboard.L2, Trigger(SDLGamepadAxis.LeftTrigger)),
            R2 = Math.Max(keyboard.R2, Trigger(SDLGamepadAxis.RightTrigger)),
            LeftX = Combine(keyboard.LeftX, SDL.GetGamepadAxis(_gamepad, SDLGamepadAxis.Leftx)),
            LeftY = Combine(keyboard.LeftY, SDL.GetGamepadAxis(_gamepad, SDLGamepadAxis.Lefty)),
            RightX = Combine(keyboard.RightX, SDL.GetGamepadAxis(_gamepad, SDLGamepadAxis.Rightx)),
            RightY = Combine(keyboard.RightY, SDL.GetGamepadAxis(_gamepad, SDLGamepadAxis.Righty)),
            Touch0 = Touch(0),
            Touch1 = Touch(1),
            Motion = _hasMotion ? ReadMotion() : keyboard.Motion,
        };
    }

    public void Rumble(byte left, byte right)
    {
        if (_gamepad.IsNull)
            return;
        _feedbackActive = true;
        SDL.RumbleGamepad(_gamepad, (ushort)(left * 257), (ushort)(right * 257), 5000);
    }

    public void SetTriggerEffects(TriggerEffect left, TriggerEffect right)
    {
        if (!IsDualSense || _gamepad.IsNull)
            return;
        Span<byte> effect = stackalloc byte[DualSenseEffects.Size];
        DualSenseEffects.Write(effect, left, right);
        _feedbackActive = true;
        fixed (byte* data = effect)
            SDL.SendGamepadEffect(_gamepad, data, effect.Length);
    }

    /// <summary>Call on the render thread when streaming ends, before accepting another session's feedback.</summary>
    public void ResetFeedback()
    {
        if (!_feedbackActive || _gamepad.IsNull)
            return;
        SDL.RumbleGamepad(_gamepad, 0, 0, 0);
        if (IsDualSense)
        {
            Span<byte> effect = stackalloc byte[DualSenseEffects.Size];
            DualSenseEffects.WriteReset(effect);
            fixed (byte* data = effect)
                SDL.SendGamepadEffect(_gamepad, data, effect.Length);
        }
        _feedbackActive = false;
    }

    public void SetLightbar(byte red, byte green, byte blue) => SDL.SetGamepadLED(_gamepad, red, green, blue);

    public void Dispose() => Close();

    internal void Open(int id)
    {
        _gamepad = SDL.OpenGamepad(id);
        if (_gamepad.IsNull)
            return;
        Name = Marshal.PtrToStringUTF8((nint)SDL.GetGamepadName(_gamepad));
        IsDualSense = SDL.GetGamepadType(_gamepad) == SDLGamepadType.Ps5;
        _hasMotion = SDL.GamepadHasSensor(_gamepad, SDLSensorType.Gyro) && SDL.GamepadHasSensor(_gamepad, SDLSensorType.Accel);
        if (_hasMotion)
        {
            SDL.SetGamepadSensorEnabled(_gamepad, SDLSensorType.Gyro, true);
            SDL.SetGamepadSensorEnabled(_gamepad, SDLSensorType.Accel, true);
        }
    }

    private void Close()
    {
        ResetFeedback();
        if (!_gamepad.IsNull)
            SDL.CloseGamepad(_gamepad);
        _gamepad = default;
        Name = null;
        IsDualSense = false;
        _hasMotion = false;
    }

    private byte Trigger(SDLGamepadAxis axis) => (byte)Math.Clamp(SDL.GetGamepadAxis(_gamepad, axis) / 128, 0, 255);

    private static short Combine(short keyboard, short gamepad) => keyboard != 0 ? keyboard : gamepad;

    private TouchPoint Touch(int finger)
    {
        bool down;
        float x, y, pressure;
        if (!SDL.GetGamepadTouchpadFinger(_gamepad, 0, finger, &down, &x, &y, &pressure) || !down)
            return TouchPoint.None;
        return new TouchPoint((sbyte)finger, (ushort)(x * (TouchpadWidth - 1)), (ushort)(y * (TouchpadHeight - 1)));
    }

    private MotionState ReadMotion()
    {
        var gyro = stackalloc float[3];
        var accel = stackalloc float[3];
        SDL.GetGamepadSensorData(_gamepad, SDLSensorType.Gyro, gyro, 3);
        SDL.GetGamepadSensorData(_gamepad, SDLSensorType.Accel, accel, 3);
        return MotionState.Neutral with
        {
            GyroX = gyro[0],
            GyroY = gyro[1],
            GyroZ = gyro[2],
            AccelX = accel[0] / StandardGravity,
            AccelY = accel[1] / StandardGravity,
            AccelZ = accel[2] / StandardGravity,
        };
    }
}
