using Cord.App.Platform;

namespace Cord.App.Updating;

/// <summary>Coordinates background GitHub release polling, live patch notes, and Windows self-updates.</summary>
internal sealed class UpdateManager : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);

    private readonly Lock _sync = new();
    private readonly HttpClient _apiHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _downloadHttp = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly CancellationTokenSource _stop = new();

    private Task? _pollLoop;
    private bool _checking;
    private bool _live;
    private DateTimeOffset? _lastCheckedAt;
    private string? _lastError;
    private IReadOnlyList<PatchEntry> _entries = AppChangelog.Entries;
    private GitHubReleaseInfo? _availableUpdate;
    private bool _popupDismissed;
    private bool _forcePopup;
    private bool _updating;
    private float _updateProgress;
    private string? _updateStatus;
    private bool _updateError;

    public UpdateManager(string? repository = null) =>
        Repository = AppVersion.NormalizeRepository(repository ?? AppVersion.GitHubRepository);

    public string? Repository { get; }

    public bool IsConfigured => Repository is not null;

    public bool IsChecking
    {
        get { lock (_sync) return _checking; }
    }

    public bool IsLive
    {
        get { lock (_sync) return _live; }
    }

    public DateTimeOffset? LastCheckedAt
    {
        get { lock (_sync) return _lastCheckedAt; }
    }

    public string? LastError
    {
        get { lock (_sync) return _lastError; }
    }

    public IReadOnlyList<PatchEntry> Entries
    {
        get { lock (_sync) return _entries; }
    }

    public GitHubReleaseInfo? AvailableUpdate
    {
        get { lock (_sync) return _availableUpdate; }
    }

    public bool IsUpdating
    {
        get { lock (_sync) return _updating; }
    }

    public float UpdateProgress
    {
        get { lock (_sync) return _updateProgress; }
    }

    public string? UpdateStatus
    {
        get { lock (_sync) return _updateStatus; }
    }

    public bool UpdateError
    {
        get { lock (_sync) return _updateError; }
    }

    public bool ShouldShowPopup(string? skippedVersion)
    {
        lock (_sync)
        {
            if (_availableUpdate is null)
                return false;
            if (_forcePopup)
                return true;
            if (_popupDismissed)
                return false;
            return !string.Equals(skippedVersion, _availableUpdate.Version, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void StartBackgroundCheck()
    {
        if (!IsConfigured)
            return;

        lock (_sync)
        {
            if (_pollLoop is not null)
                return;
            _pollLoop = Task.Run(() => PollLoopAsync(_stop.Token), CancellationToken.None);
        }
    }

    public void RefreshNow()
    {
        if (!IsConfigured)
            return;

        lock (_sync)
        {
            if (_checking)
                return;
        }

        _ = CheckOnceAsync(_stop.Token);
    }

    public void DismissPopupForSession()
    {
        lock (_sync)
        {
            _popupDismissed = true;
            _forcePopup = false;
        }
    }

    public void ReopenUpdatePopup()
    {
        lock (_sync)
        {
            if (_availableUpdate is not null)
            {
                _popupDismissed = false;
                _forcePopup = true;
                _updateError = false;
                _updateStatus = null;
            }
        }
    }

    public void BeginInstallUpdate(Action onReadyToExit)
    {
        GitHubReleaseInfo? release;
        lock (_sync)
        {
            if (_updating || _availableUpdate is null)
                return;
            release = _availableUpdate;
            _updating = true;
            _updateProgress = 0f;
            _updateError = false;
            _updateStatus = "Preparing update...";
        }

        var asset = GitHubUpdateService.SelectPlatformAsset(release.Assets);
        if (asset is null || !AppUpdater.CanSelfUpdateWindows())
        {
            if (!string.IsNullOrWhiteSpace(release.HtmlUrl))
                SdlHost.OpenUrl(release.HtmlUrl);

            lock (_sync)
            {
                _updating = false;
                _updateError = false;
                _updateStatus = "Opened the GitHub release page in your browser to download the update.";
            }

            return;
        }

        _ = Task.Run(async () =>
        {
            var (relaunch, message) = await AppUpdater.DownloadAndApplyWindowsUpdateAsync(
                _downloadHttp,
                release,
                asset,
                progress =>
                {
                    lock (_sync)
                    {
                        _updateProgress = progress;
                        _updateStatus = progress < 0.9f
                            ? $"Downloading {asset.Name}..."
                            : "Extracting update...";
                    }
                },
                _stop.Token);

            lock (_sync)
            {
                _updating = false;
                _updateError = !relaunch;
                _updateStatus = message;
            }

            if (relaunch)
                onReadyToExit();
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _apiHttp.Dispose();
        _downloadHttp.Dispose();
        _stop.Dispose();
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        await CheckOnceAsync(cancellationToken);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await CheckOnceAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        if (Repository is null)
            return;

        lock (_sync)
        {
            if (_checking)
                return;
            _checking = true;
            _lastError = null;
        }

        try
        {
            var result = await GitHubUpdateService.FetchAsync(_apiHttp, Repository, AppVersion.Current, cancellationToken);
            lock (_sync)
            {
                _lastCheckedAt = DateTimeOffset.UtcNow;
                _lastError = result.Error;
                if (result.Error is null)
                {
                    _live = true;
                    _entries = MergeEntries(result.Releases);
                    _availableUpdate = result.UpdateAvailable ? result.LatestRelease : null;
                }
            }
        }
        finally
        {
            lock (_sync)
                _checking = false;
        }
    }

    private static List<PatchEntry> MergeEntries(IReadOnlyList<GitHubReleaseInfo> releases)
    {
        var merged = new List<PatchEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var release in releases)
        {
            var entry = GitHubUpdateService.ToPatchEntry(release);
            if (seen.Add(entry.Version))
                merged.Add(entry);
        }

        foreach (var embedded in AppChangelog.Entries)
        {
            if (seen.Add(embedded.Version))
                merged.Add(embedded);
        }

        return merged;
    }
}
