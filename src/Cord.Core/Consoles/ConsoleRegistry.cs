using Cord.Core.Storage;
using Cord.Protocol.Registration;

namespace Cord.Core.Consoles;

/// <summary>A console the user paired with. Secrets are stored protected.</summary>
public sealed record StoredConsole(
    string HostId,
    string Nickname,
    string? LastAddress,
    string MacAddress,
    string RegistKey,
    string RpKey,
    int RpKeyType)
{
    public RegisteredConsole ToCredentials() => new(
        SecretProtector.Unprotect(RegistKey),
        SecretProtector.Unprotect(RpKey),
        RpKeyType,
        Convert.FromHexString(MacAddress),
        Nickname);

    public static StoredConsole From(string hostId, string? address, RegisteredConsole registered, string fallbackName) => new(
        hostId,
        registered.Nickname ?? fallbackName,
        address,
        Convert.ToHexString(registered.MacAddress),
        SecretProtector.Protect(registered.RegistKey),
        SecretProtector.Protect(registered.RpKey),
        registered.RpKeyType);
}

/// <summary>Persists registered consoles in consoles.json.</summary>
public sealed class ConsoleRegistry
{
    private readonly string _path;
    private readonly Lock _lock = new();
    private List<StoredConsole> _consoles;

    public ConsoleRegistry(AppPaths paths)
    {
        _path = paths.ConsolesFile;
        _consoles = JsonFile.Load<List<StoredConsole>>(_path, () => []);
    }

    public IReadOnlyList<StoredConsole> Consoles
    {
        get
        {
            lock (_lock)
                return [.. _consoles];
        }
    }

    public StoredConsole? Find(string hostId)
    {
        lock (_lock)
            return _consoles.FirstOrDefault(c => string.Equals(c.HostId, hostId, StringComparison.OrdinalIgnoreCase));
    }

    public void Save(StoredConsole console)
    {
        lock (_lock)
        {
            _consoles = [.. _consoles.Where(c => !string.Equals(c.HostId, console.HostId, StringComparison.OrdinalIgnoreCase)), console];
            JsonFile.Save(_path, _consoles, restrictAccess: true);
        }
    }

    public void UpdateAddress(string hostId, string address)
    {
        var existing = Find(hostId);
        if (existing is not null && existing.LastAddress != address)
            Save(existing with { LastAddress = address });
    }

    public void Remove(string hostId)
    {
        lock (_lock)
        {
            _consoles = [.. _consoles.Where(c => !string.Equals(c.HostId, hostId, StringComparison.OrdinalIgnoreCase))];
            JsonFile.Save(_path, _consoles, restrictAccess: true);
        }
    }
}
