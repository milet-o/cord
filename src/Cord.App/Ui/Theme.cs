using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;

namespace Cord.App.Ui;

/// <summary>Hardware-minimalist color tokens, type scale, and live Obsidian Dark / PS1 Light themes for Cord.</summary>
internal static class Theme
{
    public const float BodySize = 17;
    public const float SmallSize = 15;
    public const float HeadingSize = 21;
    public const float TitleSize = 30;
    public const float HeroSize = 56;

    /// <summary>Zero-terminated range list; the atlas keeps the pointer, so it is never freed.</summary>
    private static readonly unsafe uint* PrivateUseArea = CreateRanges(0xE000, 0xF8FF);

    private static float _appliedScale = 1f;

    public static bool IsLight { get; private set; }

    public static float CurrentScale { get; private set; } = 1f;

    public static Vector4 Background { get; private set; } = Rgb(0x0F1114);

    public static Vector4 BackgroundTop { get; private set; } = Rgb(0x14171C);

    public static Vector4 Surface { get; private set; } = Rgb(0x171A1F);

    public static Vector4 SurfaceHover { get; private set; } = Rgb(0x1F232A);

    public static Vector4 SurfaceRaised { get; private set; } = Rgb(0x232730);

    public static Vector4 InputBg { get; private set; } = Rgb(0x111318);

    public static Vector4 Border { get; private set; } = Rgb(0x2A2F3A);

    public static Vector4 BorderStrong { get; private set; } = Rgb(0x3A4150);

    public static Vector4 Text { get; private set; } = Rgb(0xF2F3F5);

    public static Vector4 TextMuted { get; private set; } = Rgb(0x969DA9);

    public static Vector4 TextFaint { get; private set; } = Rgb(0x636A76);

    public static Vector4 Accent { get; private set; } = Rgb(0x1B64F2);

    public static Vector4 AccentHover { get; private set; } = Rgb(0x3376F6);

    public static Vector4 AccentActive { get; private set; } = Rgb(0x1552CC);

    public static Vector4 AccentSoft { get; private set; } = Rgb(0x1B64F2, 0.14f);

    public static Vector4 AccentText { get; private set; } = Rgb(0x5B92FF);

    public static Vector4 Success { get; private set; } = Rgb(0x28B872);

    public static Vector4 Warning { get; private set; } = Rgb(0xE09F1F);

    public static Vector4 Danger { get; private set; } = Rgb(0xE04848);

    public static Vector4 Shade { get; private set; } = Rgb(0x000000, 0.62f);

    public static Vector4 ButtonSecondaryHover { get; private set; } = Rgb(0x2B303B);

    public static Vector4 ButtonSecondaryActive { get; private set; } = Rgb(0x343A47);

    public static Vector4 OnAccent { get; private set; } = Rgb(0xFFFFFF);

    public static ImFontPtr Regular { get; private set; }

    public static ImFontPtr SemiBold { get; private set; }

    /// <summary>Converts a size in design units to pixels at the current content scale.</summary>
    public static float Dp(float value) => value * ImGui.GetStyle().FontScaleDpi;

    public static Vector2 Dp(float x, float y) => new(Dp(x), Dp(y));

    public static uint U32(Vector4 color) => ImGui.ColorConvertFloat4ToU32(color);

    public static Vector4 WithAlpha(Vector4 color, float alpha) => color with { W = alpha };

    /// <summary>Loads Inter (text) with Lucide (icons) merged into each weight.</summary>
    public static void LoadFonts(ImGuiIOPtr io)
    {
        var icons = ReadAsset("Lucide.ttf");
        Regular = AddFont(io.Fonts, ReadAsset("Inter-Regular.ttf"), merge: false);
        AddFont(io.Fonts, icons, merge: true);
        SemiBold = AddFont(io.Fonts, ReadAsset("Inter-SemiBold.ttf"), merge: false);
        AddFont(io.Fonts, icons, merge: true);
        io.FontDefault = Regular;
    }

    /// <summary>Applies either Obsidian Dark or PS1 Warm Concrete Light palette at <paramref name="scale"/>.</summary>
    public static void Apply(float scale, bool light = false)
    {
        CurrentScale = scale;
        IsLight = light;
        SetPalette(light);

        var style = ImGui.GetStyle();
        if (light)
            ImGui.StyleColorsLight(style);
        else
            ImGui.StyleColorsDark(style);

        if (MathF.Abs(scale - _appliedScale) > 0.001f)
        {
            style.ScaleAllSizes(scale / _appliedScale);
            _appliedScale = scale;
        }

        style.FontSizeBase = BodySize;
        style.WindowPadding = new Vector2(16 * scale, 14 * scale);
        style.WindowBorderSize = 0;
        style.ChildBorderSize = MathF.Max(1f, MathF.Round(scale));
        style.PopupBorderSize = MathF.Max(1f, MathF.Round(scale));
        style.FrameBorderSize = MathF.Max(1f, MathF.Round(scale));
        style.WindowRounding = 10 * scale;
        style.ChildRounding = 10 * scale;
        style.PopupRounding = 8 * scale;
        style.FrameRounding = 7 * scale;
        style.GrabRounding = 6 * scale;
        style.ScrollbarRounding = 6 * scale;
        style.FramePadding = new Vector2(13 * scale, 9 * scale);
        style.ItemSpacing = new Vector2(10 * scale, 10 * scale);
        style.ItemInnerSpacing = new Vector2(8 * scale, 6 * scale);
        style.ScrollbarSize = 11 * scale;
        style.GrabMinSize = 16 * scale;
        style.DisabledAlpha = 0.45f;
        style.FontScaleDpi = scale;

        var colors = style.Colors;
        colors[(int)ImGuiCol.Text] = Text;
        colors[(int)ImGuiCol.TextDisabled] = TextFaint;
        colors[(int)ImGuiCol.WindowBg] = Background;
        colors[(int)ImGuiCol.ChildBg] = Surface;
        colors[(int)ImGuiCol.PopupBg] = Surface;
        colors[(int)ImGuiCol.ModalWindowDimBg] = Shade;
        colors[(int)ImGuiCol.Border] = Border;
        colors[(int)ImGuiCol.BorderShadow] = Rgb(0x000000, 0);
        colors[(int)ImGuiCol.FrameBg] = InputBg;
        colors[(int)ImGuiCol.FrameBgHovered] = SurfaceHover;
        colors[(int)ImGuiCol.FrameBgActive] = SurfaceRaised;
        colors[(int)ImGuiCol.Button] = SurfaceRaised;
        colors[(int)ImGuiCol.ButtonHovered] = ButtonSecondaryHover;
        colors[(int)ImGuiCol.ButtonActive] = ButtonSecondaryActive;
        colors[(int)ImGuiCol.Header] = SurfaceRaised;
        colors[(int)ImGuiCol.HeaderHovered] = ButtonSecondaryHover;
        colors[(int)ImGuiCol.HeaderActive] = ButtonSecondaryActive;
        colors[(int)ImGuiCol.CheckMark] = Accent;
        colors[(int)ImGuiCol.SliderGrab] = Accent;
        colors[(int)ImGuiCol.SliderGrabActive] = AccentHover;
        colors[(int)ImGuiCol.ScrollbarBg] = Rgb(0x000000, 0);
        colors[(int)ImGuiCol.ScrollbarGrab] = Border;
        colors[(int)ImGuiCol.ScrollbarGrabHovered] = BorderStrong;
        colors[(int)ImGuiCol.ScrollbarGrabActive] = TextFaint;
        colors[(int)ImGuiCol.Separator] = Border;
        colors[(int)ImGuiCol.TextSelectedBg] = WithAlpha(Accent, 0.28f);
        colors[(int)ImGuiCol.NavCursor] = Accent;
    }

    private static void SetPalette(bool light)
    {
        if (light)
        {
            Background = Rgb(0xE8E6E1);
            BackgroundTop = Rgb(0xDFDDD7);
            Surface = Rgb(0xF4F3EF);
            SurfaceHover = Rgb(0xEAE8E2);
            SurfaceRaised = Rgb(0xE1DFD9);
            InputBg = Rgb(0xFFFFFF);
            Border = Rgb(0xCECBC3);
            BorderStrong = Rgb(0xB5B1A7);
            Text = Rgb(0x15171A);
            TextMuted = Rgb(0x555A64);
            TextFaint = Rgb(0x7E828C);
            Accent = Rgb(0x1456D8);
            AccentHover = Rgb(0x1E63EA);
            AccentActive = Rgb(0x0F44B0);
            AccentSoft = Rgb(0x1456D8, 0.11f);
            AccentText = Rgb(0x1456D8);
            Success = Rgb(0x15804A);
            Warning = Rgb(0xB4690E);
            Danger = Rgb(0xC82E2E);
            Shade = Rgb(0x0F1114, 0.45f);
            ButtonSecondaryHover = Rgb(0xD5D2CA);
            ButtonSecondaryActive = Rgb(0xC7C4BB);
            OnAccent = Rgb(0xFFFFFF);
        }
        else
        {
            Background = Rgb(0x0F1114);
            BackgroundTop = Rgb(0x14171C);
            Surface = Rgb(0x171A1F);
            SurfaceHover = Rgb(0x1F232A);
            SurfaceRaised = Rgb(0x232730);
            InputBg = Rgb(0x111318);
            Border = Rgb(0x2A2F3A);
            BorderStrong = Rgb(0x3A4150);
            Text = Rgb(0xF2F3F5);
            TextMuted = Rgb(0x969DA9);
            TextFaint = Rgb(0x636A76);
            Accent = Rgb(0x1B64F2);
            AccentHover = Rgb(0x3376F6);
            AccentActive = Rgb(0x1552CC);
            AccentSoft = Rgb(0x1B64F2, 0.14f);
            AccentText = Rgb(0x5B92FF);
            Success = Rgb(0x28B872);
            Warning = Rgb(0xE09F1F);
            Danger = Rgb(0xE04848);
            Shade = Rgb(0x000000, 0.62f);
            ButtonSecondaryHover = Rgb(0x2B303B);
            ButtonSecondaryActive = Rgb(0x343A47);
            OnAccent = Rgb(0xFFFFFF);
        }
    }

    private static unsafe ImFontPtr AddFont(ImFontAtlasPtr atlas, byte[] data, bool merge)
    {
        var config = ImGui.ImFontConfig();
        config.MergeMode = merge;
        // Inter has its own glyphs in the private use area, which would hide the icons.
        if (!merge)
            config.GlyphExcludeRanges = PrivateUseArea;
        // Icons sit slightly high next to Inter's x-height.
        config.GlyphOffset = merge ? new Vector2(0, 3) : Vector2.Zero;
        // The atlas owns and frees the copy, so it must come from ImGui's allocator.
        var memory = ImGui.MemAlloc((nuint)data.Length);
        data.CopyTo(new Span<byte>(memory, data.Length));
        var font = atlas.AddFontFromMemoryTTF(memory, data.Length, BodySize, config);
        config.Destroy();
        return font;
    }

    private static unsafe uint* CreateRanges(uint first, uint last)
    {
        var ranges = (uint*)NativeMemory.Alloc(3, sizeof(uint));
        ranges[0] = first;
        ranges[1] = last;
        ranges[2] = 0;
        return ranges;
    }

    private static byte[] ReadAsset(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Cord.App.Assets.Fonts.{name}")
            ?? throw new InvalidOperationException($"Missing embedded font {name}.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static Vector4 Rgb(uint rgb, float alpha = 1) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);
}
