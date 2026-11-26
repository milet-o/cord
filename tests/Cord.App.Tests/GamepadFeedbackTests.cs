using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hexa.NET.SDL3;
using Cord.App.Input;
using Cord.Protocol.Stream;

namespace Cord.App.Tests;

[CollectionDefinition("SDL gamepad", DisableParallelization = true)]
public sealed class SdlGamepadTestsGroup;

[Collection("SDL gamepad")]
public sealed unsafe class GamepadFeedbackTests : IDisposable
{
    private readonly List<byte[]> _effects = [];
    private readonly GCHandle _handle;
    private readonly int _id;

    public GamepadFeedbackTests()
    {
        Assert.True(SDL.InitSubSystem(SDL.SDL_INIT_GAMEPAD));
        _handle = GCHandle.Alloc(_effects);
        var name = "Cord virtual DualSense\0"u8;
        fixed (byte* nameData = name)
        {
            var descriptor = new SDLVirtualJoystickDesc
            {
                Version = (uint)sizeof(SDLVirtualJoystickDesc),
                Type = (ushort)SDLJoystickType.Gamepad,
                VendorId = 0x054C,
                ProductId = 0x0CE6,
                Naxes = 6,
                Nbuttons = 16,
                Name = nameData,
                Userdata = (void*)GCHandle.ToIntPtr(_handle),
                SendEffect = (delegate* unmanaged[Cdecl]<void*, void*, int, byte>)&ReceiveEffect,
            };
            _id = SDL.AttachVirtualJoystick(&descriptor);
        }
        Assert.NotEqual(0, _id);
    }

    [Fact]
    public void TriggerEffects_AreSentThroughSdlAndResetAtSessionEnd()
    {
        using var gamepad = new GamepadInput();
        gamepad.Open(_id);
        Assert.True(gamepad.IsDualSense);
        Assert.True(gamepad.Connected);

        gamepad.SetTriggerEffects(new TriggerEffect(0x21, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]),
            new TriggerEffect(0x26, [11, 12, 13, 14, 15, 16, 17, 18, 19, 20]));
        gamepad.ResetFeedback();
        gamepad.ResetFeedback();

        Assert.Equal(2, _effects.Count);
        Assert.Equal(0x0C, _effects[0][0]);
        Assert.Equal(0x26, _effects[0][10]);
        Assert.Equal(new byte[] { 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 }, _effects[0][11..21]);
        Assert.Equal(0x21, _effects[0][21]);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, _effects[0][22..32]);
        AssertReset(_effects[1]);
    }

    [Fact]
    public void ClosingGamepad_ResetsTriggersBeforeClosingSdlHandle()
    {
        var gamepad = new GamepadInput();
        gamepad.Open(_id);
        gamepad.SetTriggerEffects(new TriggerEffect(0x21, new byte[10]), new TriggerEffect(0x26, new byte[10]));

        gamepad.Dispose();

        Assert.False(gamepad.Connected);
        Assert.Equal(2, _effects.Count);
        AssertReset(_effects[1]);
    }

    [Fact]
    public void NoConnectedGamepad_IgnoresEffectsAndResets()
    {
        using var gamepad = new GamepadInput();
        gamepad.SetTriggerEffects(new TriggerEffect(0x21, new byte[10]), new TriggerEffect(0x26, new byte[10]));
        gamepad.ResetFeedback();
        Assert.Empty(_effects);
        Assert.False(gamepad.IsUsbDualSense);
    }

    public void Dispose()
    {
        SDL.DetachVirtualJoystick(_id);
        _handle.Free();
        SDL.QuitSubSystem(SDL.SDL_INIT_GAMEPAD);
    }

    private static void AssertReset(byte[] effect)
    {
        Assert.Equal(47, effect.Length);
        Assert.Equal(0x0C, effect[0]);
        Assert.Equal(0x05, effect[10]);
        Assert.Equal(0x05, effect[21]);
        Assert.All(effect[11..21], value => Assert.Equal(0, value));
        Assert.All(effect[22..32], value => Assert.Equal(0, value));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte ReceiveEffect(void* userdata, void* data, int size)
    {
        var effects = (List<byte[]>)GCHandle.FromIntPtr((nint)userdata).Target!;
        effects.Add(new ReadOnlySpan<byte>(data, size).ToArray());
        return 1;
    }
}
