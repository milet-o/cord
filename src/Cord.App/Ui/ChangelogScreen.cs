using System.Globalization;
using Hexa.NET.ImGui;

namespace Cord.App.Ui;

/// <summary>Displays live GitHub release patch notes merged with built-in version history.</summary>
internal sealed class ChangelogScreen
{
    private readonly AppServices _services;

    public ChangelogScreen(AppServices services) => _services = services;

    public event Action? BackRequested;

    public event Action? UpdatePopupRequested;

    public void Draw()
    {
        var updates = _services.Updates;
        var entries = updates.Entries;

        Widgets.BeginColumn("changelog", 860);
        Widgets.Space(20);
        if (Widgets.IconButton(Icons.ArrowLeft, "back", "Back to your consoles", 44))
            BackRequested?.Invoke();
        ImGui.SameLine(0, Theme.Dp(12));
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Theme.Dp(2));
        Widgets.Title("Patch Notes");

        DrawStatusBar(updates, entries.Count);
        Widgets.Space(10);

        if (updates.AvailableUpdate is { } available)
        {
            if (Widgets.Banner("update-banner", Icons.Sparkles, $"Cord v{available.Version} is available on GitHub! Click 'Update now' to see what changed and install.", Theme.Success, "Update now"))
                UpdatePopupRequested?.Invoke();
            Widgets.Space(10);
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            Widgets.BeginCard(entry.Version);
            var headerColor = i == 0 ? Theme.AccentText : Theme.TextMuted;
            Widgets.Text($"{entry.Version}  ·  {entry.DateTime}", Theme.SmallSize, headerColor, bold: true);
            Widgets.Space(2);
            Widgets.Text(entry.Title, Theme.HeadingSize - 2, bold: true);
            Widgets.Space(4);
            foreach (var change in entry.Changes)
                Widgets.Text($"•  {change}", Theme.SmallSize, Theme.TextMuted);
            Widgets.EndCard();
            Widgets.Space(12);
        }

        Widgets.Space(28);
        Widgets.EndColumn();
    }

    private static void DrawStatusBar(Updating.UpdateManager updates, int entryCount)
    {
        var countText = $"Cord {AppVersion.Display}  ·  {entryCount.ToString(CultureInfo.InvariantCulture)} updates";
        if (updates.IsChecking)
        {
            Widgets.Progress($"{countText}  ·  Syncing releases from GitHub...");
            return;
        }

        if (updates.IsLive && updates.Repository is not null)
            Widgets.Text($"{countText}  ·  Live from github.com/{updates.Repository}", Theme.SmallSize, Theme.Success);
        else if (updates.LastError is { } error && updates.Repository is not null)
            Widgets.Text($"{countText}  ·  Offline history ({error})", Theme.SmallSize, Theme.Warning);
        else
            Widgets.Muted($"{countText}  ·  Built-in release history", Theme.SmallSize);

        if (updates.IsConfigured)
        {
            Widgets.Space(4);
            if (Widgets.Button($"{Icons.Search}  Check GitHub now", ButtonKind.Secondary))
                updates.RefreshNow();
        }
    }
}
