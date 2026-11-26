using System.Collections.Concurrent;
using Cord.Protocol.Input;
using Cord.Protocol.Takion;
using static Cord.Protocol.Tests.Support.TestBytes;

namespace Cord.Protocol.Tests.Input;

public class InputTests
{
    [Fact]
    public void WriteState_MatchesReference()
    {
        // Expected bytes were produced by a reference client for this state.
        var state = ControllerState.Idle with
        {
            LeftX = 1000,
            LeftY = -2000,
            RightX = 32767,
            RightY = -32768,
            Motion = new MotionState(1.5f, -2.0f, 0.25f, 0.1f, 1.0f, -0.5f, 0.1f, 0.2f, 0.3f, 0.927362f),
        };
        var buffer = new byte[FeedbackEncoding.StateSize];

        FeedbackEncoding.WriteState(state, buffer);

        Assert.Equal(Hex("a06586777710818e82999932731e79742d03e8f8307fff8000000001"), buffer);
    }

    [Fact]
    public void ButtonEvents_UseDocumentedCodes()
    {
        Assert.Equal(new byte[] { 0x80, 0x88, 0xFF }, FeedbackEncoding.ButtonEvent(ControllerButtons.Cross, true).Bytes);
        Assert.Equal(new byte[] { 0x80, 0x80, 0x00 }, FeedbackEncoding.ButtonEvent(ControllerButtons.DpadUp, false).Bytes);
        Assert.Equal(new byte[] { 0x80, 0xAE }, FeedbackEncoding.ButtonEvent(ControllerButtons.PS, true).Bytes);
        Assert.Equal(new byte[] { 0x80, 0x91 }, FeedbackEncoding.ButtonEvent(ControllerButtons.Touchpad, false).Bytes);
        Assert.Equal(new byte[] { 0x80, 0x87, 0x40 }, FeedbackEncoding.TriggerEvent(true, 0x40).Bytes);
    }

    [Fact]
    public void TouchEvent_PacksTwelveBitCoordinates()
    {
        Assert.Equal(new byte[] { 0xD0, 0x01, 0x78, 0x04, 0x38 }, FeedbackEncoding.TouchEvent(true, 1, 1920, 1080).Bytes);
    }

    [Fact]
    public void Diff_OrdersReleasesTouchesPresses()
    {
        var previous = ControllerState.Idle with { Buttons = ControllerButtons.Touchpad, Touch0 = new TouchPoint(0, 100, 200) };
        var current = ControllerState.Idle with { Buttons = ControllerButtons.Cross, R2 = 10 };

        var events = HistoryRecorder.Diff(previous, current).Select(e => e.Bytes).ToList();

        Assert.Equal(4, events.Count);
        Assert.Equal(new byte[] { 0x80, 0x91 }, events[0]);
        Assert.Equal(0xC0, events[1][0]);
        Assert.Equal(new byte[] { 0x80, 0x88, 0xFF }, events[2]);
        Assert.Equal(new byte[] { 0x80, 0x87, 10 }, events[3]);
    }

    [Fact]
    public async Task FeedbackSender_SendsHistoryWithNewestSequenceAndTailResends()
    {
        var transport = new RecordingTransport();
        await using (var sender = new FeedbackSender(transport))
        {
            sender.Update(ControllerState.Idle with { Buttons = ControllerButtons.Cross });
            sender.Update(ControllerState.Idle);
            await Task.Delay(450);
        }

        var history = transport.Packets.Where(p => p.Type == TakionPacketType.FeedbackHistory).ToList();
        Assert.True(history.Count >= 2);
        var newest = history[^1];
        Assert.Equal(1, newest.Sequence);
        Assert.Equal(1, newest.CountMinusOne);
        Assert.Equal(new byte[] { 0x80, 0x88, 0x00, 0x80, 0x88, 0xFF }, newest.Payload);
        Assert.True(history.Count(p => p.Sequence == 1) >= 4, "tail resends");
        Assert.Contains(transport.Packets, p => p.Type == TakionPacketType.FeedbackState);
    }

    [Fact]
    public async Task FeedbackSender_SendsStateAtLeastEvery200Milliseconds()
    {
        var transport = new RecordingTransport();
        await using (new FeedbackSender(transport))
            await Task.Delay(700);

        var states = transport.Packets.Count(p => p.Type == TakionPacketType.FeedbackState);
        Assert.InRange(states, 3, 6);
    }

    [Fact]
    public async Task FeedbackSender_Supports250HzRapidStateUpdates()
    {
        var transport = new RecordingTransport();
        await using (var sender = new FeedbackSender(transport))
        {
            for (short i = 1; i <= 15; i++)
            {
                sender.Update(ControllerState.Idle with { LeftX = (short)(i * 1000) });
                await Task.Delay(5);
            }
        }

        var states = transport.Packets.Count(p => p.Type == TakionPacketType.FeedbackState);
        Assert.True(states >= 6, $"Expected at least 6 state packets over ~75ms at 4ms cadence, got {states}");
    }

    [Fact]
    public async Task FeedbackSender_Supports500HzRapidStateUpdatesForAudioOnlyMode()
    {
        var transport = new RecordingTransport();
        await using (var sender = new FeedbackSender(transport, stateMinInterval: TimeSpan.FromMilliseconds(2)))
        {
            for (short i = 1; i <= 30; i++)
            {
                sender.Update(ControllerState.Idle with { LeftX = (short)(i * 500) });
                await Task.Delay(3);
            }
        }

        var states = transport.Packets.Count(p => p.Type == TakionPacketType.FeedbackState);
        Assert.True(states >= 12, $"Expected at least 12 state packets at 2ms cadence, got {states}");
        var audioOpts = Cord.Protocol.Stream.StreamOptions.ForAudioAndController(dualSense: true);
        Assert.True(audioOpts.AudioOnly);
        Assert.True(audioOpts.DualSense);
        Assert.Equal(1000, audioOpts.BitrateKbps);
    }

    private sealed class RecordingTransport : IFeedbackTransport
    {
        public ConcurrentQueue<(TakionPacketType Type, ushort Sequence, byte CountMinusOne, byte[] Payload)> Packets { get; } = new();

        public Task SendFeedbackAsync(TakionPacketType type, ushort sequenceNumber, byte countMinusOne, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            Packets.Enqueue((type, sequenceNumber, countMinusOne, payload.ToArray()));
            return Task.CompletedTask;
        }
    }
}
