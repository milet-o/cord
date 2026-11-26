using System.Security.Cryptography;
using System.Text;

namespace Cord.Core.Storage;

/// <summary>Encrypts and decrypts short user-configured secrets such as tokens and passwords.</summary>
public static class SecretText
{
    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
            return null;
        return SecretProtector.Protect(Encoding.UTF8.GetBytes(plaintext.Trim()));
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;
        try
        {
            return Encoding.UTF8.GetString(SecretProtector.Unprotect(stored));
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return null;
        }
    }
}
