namespace Cord.Protocol.Tests;

public class PsnAccountIdTests
{
    [Fact]
    public void FromUserId_SerializesLittleEndian()
    {
        var id = PsnAccountId.FromUserId(0x0102030405060708);

        Assert.Equal(new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 }, id.ToBytes());
        Assert.Equal("CAcGBQQDAgE=", id.ToBase64());
    }

    [Fact]
    public void FromBase64_RoundTrips()
    {
        Assert.Equal("CAcGBQQDAgE=", PsnAccountId.FromBase64("CAcGBQQDAgE=").ToBase64());
    }

    [Fact]
    public void FromBase64_RejectsWrongLength()
    {
        Assert.Throws<FormatException>(() => PsnAccountId.FromBase64("AQID"));
    }
}
