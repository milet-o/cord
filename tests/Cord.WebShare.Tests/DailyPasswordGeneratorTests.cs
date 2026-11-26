namespace Cord.WebShare.Tests;

public sealed class DailyPasswordGeneratorTests
{
    [Fact]
    public void Compute_IsDeterministicForSameDateAndEpoch()
    {
        var date = new DateOnly(2026, 11, 25);
        var a = DailyPasswordGenerator.Compute("owner-secret-key", date, epoch: 0);
        var b = DailyPasswordGenerator.Compute("owner-secret-key", date, epoch: 0);

        Assert.Equal(6, a.Length);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_ChangesAcrossDatesEpochsAndSecrets()
    {
        var d1 = new DateOnly(2026, 11, 25);
        var d2 = new DateOnly(2026, 11, 26);

        var c1 = DailyPasswordGenerator.Compute("secret-a", d1, 0);
        var c2 = DailyPasswordGenerator.Compute("secret-a", d2, 0);
        var c3 = DailyPasswordGenerator.Compute("secret-a", d1, 1);
        var c4 = DailyPasswordGenerator.Compute("secret-b", d1, 0);

        Assert.NotEqual(c1, c2);
        Assert.NotEqual(c1, c3);
        Assert.NotEqual(c1, c4);
    }

    [Fact]
    public void Verify_AcceptsTrimmedCaseInsensitiveMatchAndRejectsWrongCode()
    {
        var date = new DateOnly(2026, 11, 25);
        var code = DailyPasswordGenerator.Compute("owner-secret", date, 2);

        Assert.True(DailyPasswordGenerator.Verify(" " + code.ToLowerInvariant() + " ", "owner-secret", date, 2));
        Assert.False(DailyPasswordGenerator.Verify("WRONG1", "owner-secret", date, 2));
        Assert.False(DailyPasswordGenerator.Verify("", "owner-secret", date, 2));
    }
}
