using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cord.WebShare;

/// <summary>
/// Derives a daily-rotating 6-character access code from the owner's persistent admin secret so
/// shared links remain protected without a fixed password leaking across days.
/// </summary>
public static class DailyPasswordGenerator
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 6;

    public static string Compute(string secretKey, DateOnly date, int epoch = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);

        var keyBytes = Encoding.UTF8.GetBytes(secretKey);
        var message = Encoding.UTF8.GetBytes($"{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}:{epoch.ToString(CultureInfo.InvariantCulture)}");
        var hash = HMACSHA256.HashData(keyBytes, message);

        Span<char> chars = stackalloc char[CodeLength];
        for (var i = 0; i < CodeLength; i++)
            chars[i] = Alphabet[hash[i] % Alphabet.Length];
        return new string(chars);
    }

    public static string ComputeToday(string secretKey, int epoch = 0, TimeProvider? time = null)
    {
        var now = (time ?? TimeProvider.System).GetLocalNow();
        return Compute(secretKey, DateOnly.FromDateTime(now.DateTime), epoch);
    }

    public static bool Verify(string? candidate, string secretKey, DateOnly date, int epoch = 0)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(secretKey))
            return false;

        var normalized = candidate.Trim().ToUpperInvariant();
        var expected = Compute(secretKey, date, epoch);
        var candidateBytes = Encoding.UTF8.GetBytes(normalized);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return candidateBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(candidateBytes, expectedBytes);
    }
}
