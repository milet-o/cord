using Cord.Core.Storage;
using Cord.Protocol;

namespace Cord.Core.Account;

public sealed record StoredAccount(string AccountId, string? OnlineName, string? RefreshToken);

/// <summary>Persists the PSN account id used for registration.</summary>
public sealed class AccountStore
{
    private readonly string _path;
    private StoredAccount? _account;

    public AccountStore(AppPaths paths)
    {
        _path = paths.AccountFile;
        _account = JsonFile.Load<StoredAccount?>(_path, () => null);
    }

    public PsnAccountId? AccountId
    {
        get
        {
            if (_account is null)
                return null;
            try
            {
                return PsnAccountId.FromBase64(_account.AccountId);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    public string? OnlineName => _account?.OnlineName;

    public void Save(PsnAccountId accountId, string? onlineName = null, string? refreshToken = null)
    {
        var protectedToken = refreshToken is null ? null : SecretProtector.Protect(System.Text.Encoding.UTF8.GetBytes(refreshToken));
        _account = new StoredAccount(accountId.ToBase64(), onlineName, protectedToken);
        JsonFile.Save(_path, _account, restrictAccess: true);
    }

    public void Clear()
    {
        _account = null;
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
