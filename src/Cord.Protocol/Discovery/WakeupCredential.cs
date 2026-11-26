using System.Globalization;
using System.Text;

namespace Cord.Protocol.Discovery;

public static class WakeupCredential
{
    private const int MaxHexDigits = 8;

    public static ulong FromRegistKey(ReadOnlySpan<byte> registKey)
    {
        var nul = registKey.IndexOf((byte)0);
        var text = Encoding.ASCII.GetString(nul >= 0 ? registKey[..nul] : registKey);

        if (text.Length is 0 or > MaxHexDigits
            || !ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var credential))
        {
            throw new ArgumentException("Regist key is not a hexadecimal string of at most 8 digits.", nameof(registKey));
        }

        return credential;
    }
}
