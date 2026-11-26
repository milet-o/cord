using System.Text;
using Cord.Protocol.Discovery;

namespace Cord.Protocol.Tests.Discovery;

public class WakeupCredentialTests
{
    [Fact]
    public void FromRegistKey_ParsesHexTextUpToNul()
    {
        var registKey = new byte[16];
        Encoding.ASCII.GetBytes("1a2b3c4d", registKey);

        Assert.Equal(439041101UL, WakeupCredential.FromRegistKey(registKey));
    }

    [Fact]
    public void FromRegistKey_AcceptsKeyWithoutPadding()
    {
        Assert.Equal(0xFFFFFFFFUL, WakeupCredential.FromRegistKey("ffffffff"u8));
    }

    [Theory]
    [InlineData("")]
    [InlineData("123456789")]
    [InlineData("xyz")]
    public void FromRegistKey_RejectsInvalidKeys(string text)
    {
        Assert.Throws<ArgumentException>(() => WakeupCredential.FromRegistKey(Encoding.ASCII.GetBytes(text)));
    }
}
