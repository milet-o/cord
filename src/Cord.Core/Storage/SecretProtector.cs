using System.Security.Cryptography;

namespace Cord.Core.Storage;

/// <summary>
/// Protects secrets at rest with DPAPI on Windows. On other platforms secrets are stored as
/// is in files readable only by the user.
/// </summary>
public static class SecretProtector
{
    private const string ProtectedPrefix = "dpapi:";

    public static string Protect(byte[] secret)
    {
        if (!OperatingSystem.IsWindows())
            return Convert.ToBase64String(secret);
        return ProtectedPrefix + Convert.ToBase64String(ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser));
    }

    public static byte[] Unprotect(string stored)
    {
        if (!stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            return Convert.FromBase64String(stored);
        if (!OperatingSystem.IsWindows())
            throw new CryptographicException("This secret was protected on Windows.");
        return ProtectedData.Unprotect(Convert.FromBase64String(stored[ProtectedPrefix.Length..]), null, DataProtectionScope.CurrentUser);
    }
}
