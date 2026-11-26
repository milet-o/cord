using Hexa.NET.ImGui;
using Cord.Core.Settings;

namespace Cord.App.Ui;

/// <summary>The three quality presets as cards.</summary>
internal static class QualityPicker
{
    private sealed record Option(QualityPreset Preset, string Icon, string Title, string Summary, string Detail, string? Badge = null);

    private static readonly Option[] Options =
    [
        new(QualityPreset.Performance, Icons.Zap, "Performance", "720p  ·  60 fps  ·  10 Mbps", "Smoothest. Best on Wi-Fi or slower networks."),
        new(QualityPreset.Balanced, Icons.Gauge, "Balanced", "1080p  ·  60 fps  ·  15 Mbps", "Sharp and smooth on most home networks.", "Recommended"),
        new(QualityPreset.Quality, Icons.Gem, "Quality", "1080p  ·  60 fps  ·  30 Mbps", "Cleanest picture in fast scenes. Best with a wired connection."),
    ];

    /// <summary>Draws the presets; returns the one the user picked this frame.</summary>
    public static QualityPreset? Draw(StreamQuality current)
    {
        var selected = QualityPresets.Match(current);
        var spacing = Theme.Dp(14);
        var width = (ImGui.GetContentRegionAvail().X - spacing * (Options.Length - 1)) / Options.Length;
        QualityPreset? picked = null;
        for (var i = 0; i < Options.Length; i++)
        {
            var option = Options[i];
            if (i > 0)
                ImGui.SameLine(0, spacing);
            if (Widgets.OptionCard(option.Title, option.Icon, option.Title, option.Summary, option.Detail, option.Preset == selected, new System.Numerics.Vector2(width, Theme.Dp(176)), option.Badge))
                picked = option.Preset;
        }

        return picked;
    }
}
