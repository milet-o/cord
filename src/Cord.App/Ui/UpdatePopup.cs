using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using Cord.App.Platform;
using Cord.App.Updating;

namespace Cord.App.Ui;

/// <summary>Modal popup shown when a newer GitHub release is available, displaying live release notes and a one-click updater.</summary>
internal sealed class UpdatePopup
{
    private const string PopupId = "update-available";

    private readonly AppServices _services;

    public UpdatePopup(AppServices services) => _services = services;

    public event Action? ExitForUpdateRequested;

    public void ForceShow() => _services.Updates.ReopenUpdatePopup();

    public void Draw()
    {
        var updates = _services.Updates;
        var release = updates.AvailableUpdate;
        var skipped = _services.Settings.Current.SkippedUpdateVersion;
        if (release is null || !updates.ShouldShowPopup(skipped))
            return;

        if (!ImGui.IsPopupOpen(PopupId))
            ImGui.OpenPopup(PopupId);

        if (!Widgets.BeginDialog(PopupId, 580))
            return;

        DrawContents(updates, release);
        ImGui.EndPopup();
    }

    private void DrawContents(UpdateManager updates, GitHubReleaseInfo release)
    {
        var isUpdating = updates.IsUpdating;
        var dateLabel = release.PublishedAt == DateTimeOffset.MinValue
            ? "New release"
            : release.PublishedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

        Widgets.Text($"{Icons.Sparkles}  UPDATE AVAILABLE", Theme.SmallSize, Theme.Success, bold: true);
        Widgets.Space(2);
        Widgets.Title($"Cord v{release.Version}");
        Widgets.Muted($"Installed: {AppVersion.Display}  ·  Released: {dateLabel}", Theme.SmallSize);
        if (!string.IsNullOrWhiteSpace(release.Title) && !release.Title.Equals($"Cord v{release.Version}", StringComparison.OrdinalIgnoreCase))
        {
            Widgets.Space(2);
            Widgets.Text(release.Title, Theme.BodySize, Theme.AccentText, bold: true);
        }

        Widgets.Space(10);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.SurfaceRaised);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Dp(14, 12));
        ImGui.BeginChild("update-notes", new Vector2(Widgets.Fill, Theme.Dp(210)), ImGuiChildFlags.AlwaysUseWindowPadding);
        foreach (var line in GitHubUpdateService.ParseBodyToBullets(release.Body))
        {
            Widgets.Text($"•  {line}", Theme.SmallSize, Theme.Text);
            Widgets.Space(2);
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        Widgets.Space(10);

        if (isUpdating)
        {
            ImGui.ProgressBar(updates.UpdateProgress, new Vector2(Widgets.Fill, Theme.Dp(20)));
            Widgets.Space(4);
        }

        if (updates.UpdateStatus is { } status)
        {
            Widgets.Text(status, Theme.SmallSize, updates.UpdateError ? Theme.Danger : Theme.AccentText);
            Widgets.Space(6);
        }

        var canAutoUpdate = AppUpdater.CanSelfUpdateWindows() && GitHubUpdateService.SelectPlatformAsset(release.Assets) is not null;
        var primaryLabel = isUpdating
            ? "Updating..."
            : canAutoUpdate
                ? $"{Icons.Zap}  Update now"
                : $"{Icons.ExternalLink}  Download on GitHub";
        if (Widgets.Button(primaryLabel, ButtonKind.Primary, enabled: !isUpdating))
            updates.BeginInstallUpdate(() => ExitForUpdateRequested?.Invoke());

        if (canAutoUpdate && !string.IsNullOrWhiteSpace(release.HtmlUrl))
        {
            ImGui.SameLine();
            if (Widgets.Button($"{Icons.ExternalLink}  GitHub", ButtonKind.Secondary, enabled: !isUpdating))
                SdlHost.OpenUrl(release.HtmlUrl);
        }

        ImGui.SameLine();
        if (Widgets.Button("Later", ButtonKind.Ghost, enabled: !isUpdating))
        {
            updates.DismissPopupForSession();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (Widgets.Button("Skip version", ButtonKind.Ghost, enabled: !isUpdating))
        {
            _services.Settings.Save(_services.Settings.Current with { SkippedUpdateVersion = release.Version });
            updates.DismissPopupForSession();
            ImGui.CloseCurrentPopup();
        }
    }
}
