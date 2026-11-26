using Hexa.NET.SDL3;
using Cord.Protocol.Input;

namespace Cord.App.Input;

internal readonly record struct KeyActionDescriptor(string Id, string Label, SDLScancode DefaultKey, ControllerButtons Button);

/// <summary>
/// Configurable keyboard fallback and optional relative-mouse right-stick controller input.
/// </summary>
internal static unsafe class KeyboardInput
{
    public static readonly IReadOnlyList<KeyActionDescriptor> Actions =
    [
        new("Cross", "Cross (X)", SDLScancode.Return, ControllerButtons.Cross),
        new("Circle", "Circle (O)", SDLScancode.Backspace, ControllerButtons.Circle),
        new("Square", "Square ([])", SDLScancode.Space, ControllerButtons.Square),
        new("Triangle", "Triangle (/\\)", SDLScancode.Tab, ControllerButtons.Triangle),
        new("DpadUp", "D-pad Up", SDLScancode.Up, ControllerButtons.DpadUp),
        new("DpadDown", "D-pad Down", SDLScancode.Down, ControllerButtons.DpadDown),
        new("DpadLeft", "D-pad Left", SDLScancode.Left, ControllerButtons.DpadLeft),
        new("DpadRight", "D-pad Right", SDLScancode.Right, ControllerButtons.DpadRight),
        new("L1", "L1", SDLScancode.Q, ControllerButtons.L1),
        new("R1", "R1", SDLScancode.E, ControllerButtons.R1),
        new("L2", "L2", SDLScancode.Scancode1, ControllerButtons.None),
        new("R2", "R2", SDLScancode.Scancode3, ControllerButtons.None),
        new("L3", "L3", SDLScancode.Lshift, ControllerButtons.L3),
        new("R3", "R3", SDLScancode.C, ControllerButtons.R3),
        new("Options", "Options", SDLScancode.O, ControllerButtons.Options),
        new("Create", "Create / Share", SDLScancode.G, ControllerButtons.Create),
        new("Touchpad", "Touchpad", SDLScancode.T, ControllerButtons.Touchpad),
        new("PS", "PS Button", SDLScancode.P, ControllerButtons.PS),
        new("LeftUp", "Left Stick Up", SDLScancode.W, ControllerButtons.None),
        new("LeftDown", "Left Stick Down", SDLScancode.S, ControllerButtons.None),
        new("LeftLeft", "Left Stick Left", SDLScancode.A, ControllerButtons.None),
        new("LeftRight", "Left Stick Right", SDLScancode.D, ControllerButtons.None),
        new("RightUp", "Right Stick Up", SDLScancode.I, ControllerButtons.None),
        new("RightDown", "Right Stick Down", SDLScancode.K, ControllerButtons.None),
        new("RightLeft", "Right Stick Left", SDLScancode.J, ControllerButtons.None),
        new("RightRight", "Right Stick Right", SDLScancode.L, ControllerButtons.None),
    ];

    private static float _mouseVelX;
    private static float _mouseVelY;
    private static bool _mouseLeftDown;
    private static bool _mouseRightDown;

    public static void AddMouseMotion(float dx, float dy, float sensitivity)
    {
        var scale = 0.045f * Math.Clamp(sensitivity, 0.2f, 4.0f);
        _mouseVelX = Math.Clamp(_mouseVelX + (dx * scale), -1f, 1f);
        _mouseVelY = Math.Clamp(_mouseVelY + (dy * scale), -1f, 1f);
    }

    public static void SetMouseButton(uint button, bool down)
    {
        if (button == 1)
            _mouseLeftDown = down;
        else if (button == 3)
            _mouseRightDown = down;
    }

    public static void ResetMouse()
    {
        _mouseVelX = 0;
        _mouseVelY = 0;
        _mouseLeftDown = false;
        _mouseRightDown = false;
    }

    public static Dictionary<string, SDLScancode> ResolveBindings(IReadOnlyDictionary<string, string>? custom)
    {
        var map = new Dictionary<string, SDLScancode>(Actions.Count, StringComparer.Ordinal);
        foreach (var action in Actions)
        {
            var resolved = action.DefaultKey;
            if (custom is not null
                && custom.TryGetValue(action.Id, out var name)
                && Enum.TryParse<SDLScancode>(name, ignoreCase: true, out var parsed)
                && parsed != SDLScancode.Unknown)
            {
                resolved = parsed;
            }

            map[action.Id] = resolved;
        }

        return map;
    }

    public static string ScancodeLabel(SDLScancode scancode) => scancode switch
    {
        SDLScancode.Return => "Enter",
        SDLScancode.Backspace => "Backspace",
        SDLScancode.Space => "Space",
        SDLScancode.Tab => "Tab",
        SDLScancode.Scancode1 => "1",
        SDLScancode.Scancode2 => "2",
        SDLScancode.Scancode3 => "3",
        SDLScancode.Lshift => "L-Shift",
        SDLScancode.Rshift => "R-Shift",
        SDLScancode.Lctrl => "L-Ctrl",
        _ => scancode.ToString(),
    };

    public static ControllerState Read(IReadOnlyDictionary<string, string>? customBindings = null, bool mouseRightStick = false)
    {
        int count;
        var state = SDL.GetKeyboardState(&count);
        var keys = new ReadOnlySpan<bool>(state, count).ToArray();
        var bindings = ResolveBindings(customBindings);
        bool Down(string actionId) => bindings.TryGetValue(actionId, out var key) && (int)key < keys.Length && keys[(int)key];

        var buttons = ControllerButtons.None;
        foreach (var action in Actions)
        {
            if (action.Button != ControllerButtons.None && Down(action.Id))
                buttons |= action.Button;
        }

        var l2 = (Down("L2") || (mouseRightStick && _mouseRightDown)) ? (byte)255 : (byte)0;
        var r2 = (Down("R2") || (mouseRightStick && _mouseLeftDown)) ? (byte)255 : (byte)0;

        var rx = Axis(Down("RightLeft"), Down("RightRight"));
        var ry = Axis(Down("RightUp"), Down("RightDown"));
        if (mouseRightStick)
        {
            if (rx == 0 && MathF.Abs(_mouseVelX) > 0.02f)
                rx = FloatToAxis(_mouseVelX);
            if (ry == 0 && MathF.Abs(_mouseVelY) > 0.02f)
                ry = FloatToAxis(_mouseVelY);
            _mouseVelX *= 0.45f;
            _mouseVelY *= 0.45f;
        }

        return ControllerState.Idle with
        {
            Buttons = buttons,
            L2 = l2,
            R2 = r2,
            LeftX = Axis(Down("LeftLeft"), Down("LeftRight")),
            LeftY = Axis(Down("LeftUp"), Down("LeftDown")),
            RightX = rx,
            RightY = ry,
        };
    }

    internal static short FloatToAxis(float v) =>
        (short)Math.Clamp((int)MathF.Round(v * short.MaxValue), short.MinValue, short.MaxValue);

    private static short Axis(bool negative, bool positive) =>
        negative == positive ? (short)0 : negative ? short.MinValue : short.MaxValue;
}
