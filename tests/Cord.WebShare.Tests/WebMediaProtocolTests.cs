using System.Buffers.Binary;
using Cord.Protocol.Input;
using Cord.WebShare;

namespace Cord.WebShare.Tests;

public sealed class WebMediaProtocolTests
{
    [Fact]
    public void BuildVideoPacket_PrependsHeaderAndSetsFlagsAndTimestamp()
    {
        byte[] header = [0x00, 0x00, 0x00, 0x01, 0x67];
        byte[] frame = [0x00, 0x00, 0x00, 0x01, 0x65, 0xAA];
        var packet = WebMediaProtocol.BuildVideoPacket(frame, header, isKeyframe: true, timestampUs: 123456789L);

        Assert.Equal(WebMediaProtocol.VideoFrame, packet[0]);
        Assert.Equal(WebMediaProtocol.VideoFlagKeyframe, packet[1]);
        Assert.Equal(123456789L, BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(2, 8)));
        Assert.Equal([.. header, .. frame], packet[10..]);
    }

    [Fact]
    public void BuildAudioAndRumblePackets_MatchExpectedLayout()
    {
        byte[] opus = [1, 2, 3, 4];
        var audio = WebMediaProtocol.BuildAudioPacket(opus, 987654L);
        Assert.Equal(WebMediaProtocol.AudioFrame, audio[0]);
        Assert.Equal(0, audio[1]);
        Assert.Equal(987654L, BinaryPrimitives.ReadInt64LittleEndian(audio.AsSpan(2, 8)));
        Assert.Equal(opus, audio[10..]);

        var rumble = WebMediaProtocol.BuildRumblePacket(120, 240);
        Assert.Equal([WebMediaProtocol.Rumble, 120, 240], rumble);
    }

    [Fact]
    public void TryParseControllerInput_RoundTripsButtonsTriggersAndAxes()
    {
        var payload = new byte[WebMediaProtocol.ControllerInputSize];
        payload[0] = WebMediaProtocol.ControllerInput;
        var expectedButtons = ControllerButtons.Cross | ControllerButtons.R1 | ControllerButtons.PS;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), (uint)expectedButtons);
        payload[5] = 200;
        payload[6] = 128;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(7, 2), -16384);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(9, 2), 32767);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(11, 2), -32768);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(13, 2), 42);

        var ok = WebMediaProtocol.TryParseControllerInput(payload, out var state);

        Assert.True(ok);
        Assert.Equal(expectedButtons, state.Buttons);
        Assert.Equal(200, state.L2);
        Assert.Equal(128, state.R2);
        Assert.Equal((short)-16384, state.LeftX);
        Assert.Equal((short)32767, state.LeftY);
        Assert.Equal((short)-32768, state.RightX);
        Assert.Equal((short)42, state.RightY);
    }
}
