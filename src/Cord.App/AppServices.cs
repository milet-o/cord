using Cord.App.Updating;
using Cord.Core.Account;
using Cord.Core.Consoles;
using Cord.Core.Settings;
using Cord.Core.Storage;
using Cord.Media;
using Cord.Protocol.Crypto;
using Cord.Psn;

namespace Cord.App;

/// <summary>Application-wide services built at start-up.</summary>
internal sealed class AppServices : IAsyncDisposable
{
    public AppServices(AppPaths paths)
    {
        Paths = paths;
        Settings = new SettingsStore(Paths);
        Consoles = new ConsoleRegistry(Paths);
        Account = new AccountStore(Paths);
        Browser = new ConsoleBrowser(Consoles);
        WebShare = new ConsoleWebShare(Tables, Consoles, Browser, Settings);
        Updates = new UpdateManager();

        try
        {
            FfmpegVersion = FfmpegLibrary.Load(Settings.Current.FfmpegPath);
        }
        catch (Exception e) when (e is DllNotFoundException or NotSupportedException or EntryPointNotFoundException or InvalidOperationException)
        {
            StartupProblems.Add($"FFmpeg could not be loaded: {e.Message} Reinstall Cord or set the FFmpeg folder in Settings > Advanced.");
        }
    }

    public AppPaths Paths { get; }

    public SettingsStore Settings { get; }

    public ConsoleRegistry Consoles { get; }

    public AccountStore Account { get; }

    public ConsoleBrowser Browser { get; }

    public ConsoleSharing Sharing { get; } = new();

    public ConsoleWebShare WebShare { get; }

    public UpdateManager Updates { get; }

    public RpCryptTables Tables { get; } = RpCryptTables.Default;

    public string? FfmpegVersion { get; }

    public List<string> StartupProblems { get; } = [];

    public PsnOAuthOptions PsnOptions => PsnOAuthOptions.Resolve(Settings.Current.PsnClientId, Settings.Current.PsnClientSecret);

    /// <summary>
    /// True until the first-run setup is finished. Installations that already have an account
    /// and a paired console (from before the setup existed) do not need it.
    /// </summary>
    public bool NeedsSetup => !Settings.Current.SetupCompleted && (Account.AccountId is null || Consoles.Consoles.Count == 0);

    /// <summary>True on a brand-new installation before the interactive quick-start guide has been completed or skipped.</summary>
    public bool NeedsGuide => !Settings.Current.GuideCompleted && NeedsSetup;

    public ValueTask DisposeAsync()
    {
        Updates.Dispose();
        WebShare.Dispose();
        Sharing.Dispose();
        return Browser.DisposeAsync();
    }
}
