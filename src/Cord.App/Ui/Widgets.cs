using System.Numerics;
using Hexa.NET.ImGui;

namespace Cord.App.Ui;

internal enum ButtonKind
{
    Primary,
    Secondary,
    Ghost,
    Danger,
}

/// <summary>The launcher's building blocks, drawn on top of Dear ImGui items so keyboard and gamepad navigation keep working.</summary>
internal static class Widgets
{
    /// <summary>Item width that fills the remaining space (ImGui's -FLT_MIN convention).</summary>
    public const float Fill = -1.17549435e-38f;

    public static void Text(string text, float size = Theme.BodySize, Vector4? color = null, bool bold = false)
    {
        var escaped = text.Contains('%', StringComparison.Ordinal) ? text.Replace("%", "%%", StringComparison.Ordinal) : text;
        ImGui.PushFont(bold ? Theme.SemiBold : Theme.Regular, size);
        ImGui.PushStyleColor(ImGuiCol.Text, color ?? Theme.Text);
        ImGui.TextWrapped(escaped);
        ImGui.PopStyleColor();
        ImGui.PopFont();
    }

    public static void Title(string text) => Text(text, Theme.TitleSize, bold: true);

    public static void Heading(string text) => Text(text, Theme.HeadingSize, bold: true);

    public static void Muted(string text, float size = Theme.BodySize) => Text(text, size, Theme.TextMuted);

    /// <summary>A single line of text centered in the available width.</summary>
    public static void Centered(string text, float size = Theme.BodySize, Vector4? color = null, bool bold = false)
    {
        ImGui.PushFont(bold ? Theme.SemiBold : Theme.Regular, size);
        var width = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (ImGui.GetContentRegionAvail().X - width) / 2));
        ImGui.PushStyleColor(ImGuiCol.Text, color ?? Theme.Text);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
        ImGui.PopFont();
    }

    public static void Space(float height) => ImGui.Dummy(new Vector2(0, Theme.Dp(height)));

    /// <summary>Continues the current line so that an item of <paramref name="width"/> ends at the right edge.</summary>
    public static void SameLineRight(float width)
    {
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width);
    }

    public static bool Button(string label, ButtonKind kind = ButtonKind.Secondary, float width = 0, bool enabled = true)
    {
        var (normal, hovered, active, text) = kind switch
        {
            ButtonKind.Primary => (Theme.Accent, Theme.AccentHover, Theme.AccentActive, Theme.OnAccent),
            ButtonKind.Ghost => (Theme.WithAlpha(Theme.SurfaceRaised, 0), Theme.SurfaceHover, Theme.SurfaceRaised, Theme.TextMuted),
            ButtonKind.Danger => (Theme.WithAlpha(Theme.Danger, 0.14f), Theme.WithAlpha(Theme.Danger, 0.24f), Theme.WithAlpha(Theme.Danger, 0.32f), Theme.Danger),
            _ => (Theme.SurfaceRaised, Theme.ButtonSecondaryHover, Theme.ButtonSecondaryActive, Theme.Text),
        };
        var borderColor = kind switch
        {
            ButtonKind.Primary => Theme.AccentActive,
            ButtonKind.Ghost => Theme.WithAlpha(Theme.Border, 0),
            ButtonKind.Danger => Theme.WithAlpha(Theme.Danger, 0.45f),
            _ => Theme.Border,
        };
        ImGui.PushStyleColor(ImGuiCol.Button, normal);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
        ImGui.PushStyleColor(ImGuiCol.Text, text);
        ImGui.PushStyleColor(ImGuiCol.Border, borderColor);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, kind == ButtonKind.Ghost ? Theme.Dp(12, 10) : Theme.Dp(18, 10));
        ImGui.PushFont(Theme.SemiBold, Theme.BodySize);
        ImGui.BeginDisabled(!enabled);
        var clicked = ImGui.Button(label, new Vector2(width, 0));
        ImGui.EndDisabled();
        ImGui.PopFont();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(5);
        return clicked && enabled;
    }

    /// <summary>The width <see cref="Button"/> needs for a label.</summary>
    public static float ButtonWidth(string label)
    {
        ImGui.PushFont(Theme.SemiBold, Theme.BodySize);
        var width = ImGui.CalcTextSize(label, true).X;
        ImGui.PopFont();
        return width + Theme.Dp(40);
    }

    public static bool IconButton(string icon, string id, string tooltip, float size = 40)
    {
        var pixels = Theme.Dp(size);
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.SurfaceRaised, 0));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.SurfaceHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Theme.SurfaceRaised);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, pixels / 2);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGui.PushFont(Theme.Regular, size * 0.5f);
        var clicked = ImGui.Button($"{icon}##{id}", new Vector2(pixels, pixels));
        ImGui.PopFont();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(4);
        ImGui.SetItemTooltip(tooltip);
        return clicked;
    }

    public static void Spinner(float radius = 9, Vector4? color = null)
    {
        var r = Theme.Dp(radius);
        var thickness = Theme.Dp(2.5f);
        var position = ImGui.GetCursorScreenPos();
        var size = new Vector2((r + thickness) * 2);
        ImGui.Dummy(size);
        var start = (float)ImGui.GetTime() * 6;
        var drawList = ImGui.GetWindowDrawList();
        drawList.PathArcTo(position + size / 2, r, start, start + MathF.PI * 1.5f, 32);
        drawList.PathStroke(Theme.U32(color ?? Theme.Accent), ImDrawFlags.None, thickness);
    }

    /// <summary>A spinner followed by a line of text, vertically aligned.</summary>
    public static void Progress(string text)
    {
        var lineHeight = ImGui.GetTextLineHeight();
        var y = ImGui.GetCursorPosY();
        Spinner();
        ImGui.SameLine();
        ImGui.SetCursorPosY(y + (Theme.Dp(23) - lineHeight) / 2);
        Text(text, color: Theme.TextMuted);
    }

    public static void Dot(Vector4 color, float radius = 5)
    {
        var height = ImGui.GetTextLineHeight();
        var position = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(Theme.Dp(radius * 2), height));
        ImGui.GetWindowDrawList().AddCircleFilled(position + new Vector2(Theme.Dp(radius), height / 2), Theme.Dp(radius), Theme.U32(color));
    }

    /// <summary>A label and description with a switch on the right; the whole row toggles.</summary>
    public static bool Toggle(string label, string description, ref bool value)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var switchSize = Theme.Dp(46, 26);
        var start = ImGui.GetCursorScreenPos();

        ImGui.BeginGroup();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Theme.Dp(12, 2));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - switchSize.X - Theme.Dp(24));
        Text(label, bold: true);
        Text(description, Theme.SmallSize, Theme.TextMuted);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleVar();
        ImGui.EndGroup();
        var height = ImGui.GetItemRectSize().Y;

        ImGui.SetCursorScreenPos(start);
        var clicked = ImGui.InvisibleButton(label, new Vector2(width, height));
        if (clicked)
            value = !value;

        var drawList = ImGui.GetWindowDrawList();
        var track = new Vector2(start.X + width - switchSize.X, start.Y + (height - switchSize.Y) / 2);
        var radius = switchSize.Y / 2;
        var trackColor = value ? Theme.Accent : ImGui.IsItemHovered() ? Theme.BorderStrong : Theme.Border;
        drawList.AddRectFilled(track, track + switchSize, Theme.U32(trackColor), radius);
        var knobX = value ? track.X + switchSize.X - radius : track.X + radius;
        var knobColor = value ? Theme.OnAccent : Theme.Surface;
        drawList.AddCircleFilled(new Vector2(knobX, track.Y + radius), radius - Theme.Dp(4), Theme.U32(knobColor));
        FocusRing(start - Theme.Dp(6, 6), start + new Vector2(width, height) + Theme.Dp(6, 6), Theme.Dp(10));
        ImGui.Dummy(Theme.Dp(0, 4));
        return clicked;
    }

    /// <summary>A row of mutually exclusive choices.</summary>
    public static bool Segmented(string id, string[] items, ref int index)
    {
        var spacing = Theme.Dp(6);
        var width = (ImGui.GetContentRegionAvail().X - spacing * (items.Length - 1)) / items.Length;
        var changed = false;
        ImGui.PushID(id);
        for (var i = 0; i < items.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, spacing);
            if (Button(items[i], i == index ? ButtonKind.Primary : ButtonKind.Secondary, width) && i != index)
            {
                index = i;
                changed = true;
            }
        }

        ImGui.PopID();
        return changed;
    }

    /// <summary>A clickable card with an icon, a title and two lines of detail.</summary>
    public static unsafe bool OptionCard(string id, string icon, string title, string subtitle, string detail, bool selected, Vector2 size, string? badge = null)
    {
        var position = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var rounding = Theme.Dp(10);
        var background = selected ? Theme.AccentSoft : hovered ? Theme.SurfaceHover : Theme.Surface;
        drawList.AddRectFilled(position, position + size, Theme.U32(background), rounding);
        drawList.AddRect(position, position + size, Theme.U32(selected ? Theme.Accent : Theme.Border), rounding, ImDrawFlags.None, Theme.Dp(selected ? 2 : 1));

        var padding = Theme.Dp(18);
        var cursor = position + new Vector2(padding, padding);
        var wrap = size.X - padding * 2;
        drawList.AddText(Theme.SemiBold.Handle, Theme.Dp(26), cursor, Theme.U32(selected ? Theme.AccentText : Theme.TextMuted), icon);
        if (selected)
            drawList.AddText(Theme.SemiBold.Handle, Theme.Dp(20), new Vector2(position.X + size.X - padding - Theme.Dp(20), cursor.Y), Theme.U32(Theme.AccentText), Icons.CircleCheck);
        else if (badge is not null)
            Badge(drawList, badge, new Vector2(position.X + size.X - padding, cursor.Y));

        cursor.Y += Theme.Dp(40);
        drawList.AddText(Theme.SemiBold.Handle, Theme.Dp(Theme.HeadingSize - 2), cursor, Theme.U32(Theme.Text), title);
        cursor.Y += Theme.Dp(28);
        drawList.AddText(Theme.SemiBold.Handle, Theme.Dp(Theme.SmallSize), cursor, Theme.U32(selected ? Theme.AccentText : Theme.Text), subtitle);
        cursor.Y += Theme.Dp(24);
        drawList.AddText(Theme.Regular.Handle, Theme.Dp(Theme.SmallSize), cursor, Theme.U32(Theme.TextMuted), detail, wrap);
        FocusRing(position, position + size, rounding);
        return clicked;
    }

    /// <summary>A full-width selectable row with a round icon, a title and a detail line.</summary>
    public static unsafe bool ListRow(string id, string icon, string title, string detail, bool selected, bool enabled = true)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var size = new Vector2(width, Theme.Dp(68));
        var position = ImGui.GetCursorScreenPos();
        ImGui.BeginDisabled(!enabled);
        var clicked = ImGui.InvisibleButton(id, size);
        ImGui.EndDisabled();
        var hovered = enabled && ImGui.IsItemHovered();

        var drawList = ImGui.GetWindowDrawList();
        var rounding = Theme.Dp(10);
        var background = selected ? Theme.AccentSoft : hovered ? Theme.SurfaceHover : Theme.Surface;
        drawList.AddRectFilled(position, position + size, Theme.U32(background), rounding);
        drawList.AddRect(position, position + size, Theme.U32(selected ? Theme.Accent : Theme.Border), rounding, ImDrawFlags.None, Theme.Dp(selected ? 2 : 1));

        var padding = Theme.Dp(16);
        var middle = position.Y + size.Y / 2;
        var iconCenter = new Vector2(position.X + padding + Theme.Dp(20), middle);
        drawList.AddCircleFilled(iconCenter, Theme.Dp(20), Theme.U32(Theme.SurfaceRaised));
        DrawCentered(drawList, Theme.Regular, 20, iconCenter, enabled ? Theme.Text : Theme.TextFaint, icon);
        var textX = iconCenter.X + Theme.Dp(34);
        drawList.AddText(Theme.SemiBold.Handle, Theme.Dp(Theme.BodySize), new Vector2(textX, middle - Theme.Dp(21)), Theme.U32(enabled ? Theme.Text : Theme.TextMuted), title);
        drawList.AddText(Theme.Regular.Handle, Theme.Dp(Theme.SmallSize), new Vector2(textX, middle + Theme.Dp(2)), Theme.U32(enabled ? Theme.TextMuted : Theme.TextFaint), detail);
        if (selected || enabled)
        {
            var mark = selected ? Icons.CircleCheck : Icons.ChevronRight;
            DrawCentered(drawList, Theme.Regular, 20, new Vector2(position.X + size.X - padding - Theme.Dp(10), middle), selected ? Theme.Accent : Theme.TextFaint, mark);
        }

        FocusRing(position, position + size, rounding);
        return clicked && enabled;
    }

    /// <summary>A large outlined tile with an icon and a label, used for "add" actions.</summary>
    public static bool Tile(string id, string icon, string label, Vector2 size)
    {
        var position = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var rounding = Theme.Dp(10);
        if (hovered)
            drawList.AddRectFilled(position, position + size, Theme.U32(Theme.SurfaceHover), rounding);
        drawList.AddRect(position, position + size, Theme.U32(hovered ? Theme.BorderStrong : Theme.Border), rounding, ImDrawFlags.None, Theme.Dp(1));
        var center = position + size / 2;
        drawList.AddCircleFilled(center - Theme.Dp(0, 22), Theme.Dp(26), Theme.U32(hovered ? Theme.AccentSoft : Theme.SurfaceRaised));
        DrawCentered(drawList, Theme.Regular, 24, center - Theme.Dp(0, 22), hovered ? Theme.AccentText : Theme.TextMuted, icon);
        DrawCentered(drawList, Theme.SemiBold, Theme.BodySize, center + Theme.Dp(0, 30), hovered ? Theme.Text : Theme.TextMuted, label);
        FocusRing(position, position + size, rounding);
        return clicked;
    }

    /// <summary>Starts a centered modal without a title bar; call EndPopup only when it returns true.</summary>
    public static unsafe bool BeginDialog(string id, float width)
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size / 2, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(Theme.Dp(width), 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Dp(28, 24));
        var open = ImGui.BeginPopupModal(id, (bool*)null, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings);
        ImGui.PopStyleVar();
        return open;
    }

    /// <summary>Numbered steps joined by a line; finished steps show a check mark.</summary>
    public static unsafe void Steps(string[] names, int current)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var position = ImGui.GetCursorScreenPos();
        var radius = Theme.Dp(15);
        var slot = width / names.Length;
        var drawList = ImGui.GetWindowDrawList();
        for (var i = 0; i < names.Length; i++)
        {
            var center = new Vector2(position.X + slot * (i + 0.5f), position.Y + radius);
            if (i < names.Length - 1)
            {
                var color = i < current ? Theme.Accent : Theme.Border;
                drawList.AddLine(center + new Vector2(radius + Theme.Dp(8), 0), center + new Vector2(slot - radius - Theme.Dp(8), 0), Theme.U32(color), Theme.Dp(2));
            }

            var done = i < current;
            var active = i == current;
            drawList.AddCircleFilled(center, radius, Theme.U32(done || active ? Theme.Accent : Theme.SurfaceRaised));
            var mark = done ? Icons.Check : (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            DrawCentered(drawList, Theme.SemiBold, Theme.SmallSize, center, done || active ? new Vector4(1, 1, 1, 1) : Theme.TextMuted, mark);
            DrawCentered(drawList, active ? Theme.SemiBold : Theme.Regular, Theme.SmallSize, center + new Vector2(0, radius + Theme.Dp(16)), active ? Theme.Text : Theme.TextMuted, names[i]);
        }

        ImGui.Dummy(new Vector2(width, radius * 2 + Theme.Dp(28)));
    }

    /// <summary>A tinted message strip; returns true when its action button is pressed.</summary>
    public static bool Banner(string id, string icon, string message, Vector4 color, string? action = null)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Surface);
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.WithAlpha(color, 0.55f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Dp(18, 13));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Theme.Dp(10));
        ImGui.BeginChild(id, new Vector2(Fill, 0), ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysUseWindowPadding, ImGuiWindowFlags.NoScrollbar);
        var actionWidth = action is null ? 0 : ButtonWidth(action);
        var available = ImGui.GetContentRegionAvail().X;
        var y = ImGui.GetCursorPosY();
        var textOffset = action is null ? 0 : (ImGui.GetFrameHeight() + Theme.Dp(4) - ImGui.GetTextLineHeight()) / 2;
        ImGui.SetCursorPosY(y + textOffset);
        Text(icon, color: color);
        ImGui.SameLine();
        ImGui.SetCursorPosY(y + textOffset);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + available - actionWidth - Theme.Dp(48));
        Text(message);
        ImGui.PopTextWrapPos();
        var clicked = false;
        if (action is not null)
        {
            SameLineRight(actionWidth);
            ImGui.SetCursorPosY(y);
            clicked = Button(action);
        }

        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
        return clicked;
    }

    /// <summary>Starts a rounded surface; always pair with <see cref="EndCard"/>.</summary>
    public static void BeginCard(string id, float width = Fill, float height = 0)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Dp(24, 22));
        var flags = ImGuiChildFlags.AlwaysUseWindowPadding | (height == 0 ? ImGuiChildFlags.AutoResizeY : ImGuiChildFlags.None);
        ImGui.BeginChild(id, new Vector2(width, height), flags, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
    }

    public static void EndCard() => ImGui.EndChild();

    /// <summary>Starts a transparent column of at most <paramref name="maxWidth"/> centered in the window.</summary>
    public static void BeginColumn(string id, float maxWidth)
    {
        var available = ImGui.GetContentRegionAvail().X;
        var width = MathF.Min(available, Theme.Dp(maxWidth));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (available - width) / 2);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.BeginChild(id, new Vector2(width, 0), ImGuiChildFlags.AutoResizeY, ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
    }

    public static void EndColumn() => ImGui.EndChild();

    /// <summary>A numbered instruction line.</summary>
    public static unsafe void NumberedStep(int number, string text)
    {
        var position = ImGui.GetCursorScreenPos();
        var radius = Theme.Dp(13);
        var drawList = ImGui.GetWindowDrawList();
        var center = position + new Vector2(radius, radius);
        drawList.AddCircleFilled(center, radius, Theme.U32(Theme.AccentSoft));
        DrawCentered(drawList, Theme.SemiBold, Theme.SmallSize, center, Theme.AccentText, number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ImGui.SetCursorScreenPos(position + new Vector2(radius * 2 + Theme.Dp(14), (radius * 2 - ImGui.GetTextLineHeight()) / 2));
        ImGui.BeginGroup();
        Text(text);
        ImGui.EndGroup();
        var bottom = MathF.Max(ImGui.GetItemRectMax().Y, position.Y + radius * 2);
        ImGui.SetCursorScreenPos(new Vector2(position.X, bottom));
        ImGui.Dummy(new Vector2(0, Theme.Dp(4)));
    }

    /// <summary>Draws the keyboard/gamepad focus outline around the last item when navigation is in use.</summary>
    private static void FocusRing(Vector2 min, Vector2 max, float rounding)
    {
        if (!ImGui.IsItemFocused() || !ImGui.GetIO().NavVisible)
            return;
        var offset = Theme.Dp(3, 3);
        ImGui.GetWindowDrawList().AddRect(min - offset, max + offset, Theme.U32(Theme.Text), rounding + offset.X, ImDrawFlags.None, Theme.Dp(2));
    }

    private static unsafe void Badge(ImDrawListPtr drawList, string text, Vector2 topRight)
    {
        ImGui.PushFont(Theme.SemiBold, Theme.SmallSize - 2);
        var size = ImGui.CalcTextSize(text);
        ImGui.PopFont();
        var padding = Theme.Dp(8, 3);
        var min = topRight - new Vector2(size.X + padding.X * 2, 0);
        var max = topRight + new Vector2(0, size.Y + padding.Y * 2);
        drawList.AddRectFilled(min, max, Theme.U32(Theme.AccentSoft), Theme.Dp(6));
        drawList.AddText(Theme.SemiBold.Handle, Theme.Dp(Theme.SmallSize - 2), min + padding, Theme.U32(Theme.AccentText), text);
    }

    public static unsafe void DrawCentered(ImDrawListPtr drawList, ImFontPtr font, float size, Vector2 center, Vector4 color, string text)
    {
        ImGui.PushFont(font, size);
        var extent = ImGui.CalcTextSize(text);
        ImGui.PopFont();
        drawList.AddText(font.Handle, Theme.Dp(size), center - extent / 2, Theme.U32(color), text);
    }
}
