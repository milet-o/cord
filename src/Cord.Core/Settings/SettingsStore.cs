using System.Security.Cryptography;
using Cord.Core.Storage;

namespace Cord.Core.Settings;

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly Lock _lock = new();

    public SettingsStore(AppPaths paths)
    {
        _path = paths.SettingsFile;
        Current = JsonFile.Load(_path, () => new AppSettings());
    }

    public AppSettings Current { get; private set; }

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            Current = settings;
            JsonFile.Save(_path, settings, restrictAccess: true);
        }
    }

    /// <summary>
    /// Returns the persistent owner admin key for Web Share mobile management, creating and
    /// saving a fresh cryptographic key the first time it is needed.
    /// </summary>
    public string EnsureWebShareAdminKey()
    {
        lock (_lock)
        {
            var existing = SecretText.Unprotect(Current.WebShare.AdminKeyProtected);
            if (!string.IsNullOrWhiteSpace(existing))
                return existing;

            var bytes = RandomNumberGenerator.GetBytes(18);
            var key = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var updated = Current with
            {
                WebShare = Current.WebShare with { AdminKeyProtected = SecretText.Protect(key) },
            };
            Current = updated;
            JsonFile.Save(_path, updated, restrictAccess: true);
            return key;
        }
    }
}
