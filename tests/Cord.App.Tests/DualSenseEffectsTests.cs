using Cord.App.Input;
using Cord.Protocol.Stream;

namespace Cord.App.Tests;

public class DualSenseEffectsTests
{
    [Fact]
    public void Write_PreservesBothEffectsAndOnlyEnablesTriggers()
    {
        var report = Enumerable.Repeat((byte)0xFF, DualSenseEffects.Size).ToArray();
        var left = new TriggerEffect(0x21, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09]);
        var right = new TriggerEffect(0x26, [0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xFF]);

        DualSenseEffects.Write(report, left, right);

        Assert.Equal(Convert.FromHexString(
            "0C000000000000000000" +
            "26F0F1F2F3F4F5F6F7F8FF" +
            "2100010203040506070809" +
            "000000000000000000000000000000"), report);
    }

    [Fact]
    public void WriteReset_TurnsBothTriggersOffAndClearsPreviousParameters()
    {
        var report = Enumerable.Repeat((byte)0xFF, DualSenseEffects.Size).ToArray();

        DualSenseEffects.WriteReset(report);

        Assert.Equal(Convert.FromHexString(
            "0C000000000000000000" +
            "0500000000000000000000" +
            "0500000000000000000000" +
            "000000000000000000000000000000"), report);
    }

    [Theory]
    [InlineData(9, 10)]
    [InlineData(10, 11)]
    [InlineData(0, 0)]
    public void Write_RejectsInvalidParameterSizes(int leftSize, int rightSize)
    {
        var report = new byte[DualSenseEffects.Size];
        Assert.Throws<ArgumentException>(() => DualSenseEffects.Write(report,
            new TriggerEffect(0x21, new byte[leftSize]), new TriggerEffect(0x26, new byte[rightSize])));
        Assert.All(report, b => Assert.Equal(0, b));
    }
}
