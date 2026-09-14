using Godot;

namespace CoopBots;

/// <summary>
/// One look for every panel this mod draws: the cover art's palette (dark slate
/// with a single warm accent), the same type scale, and one card style. Both the
/// lobby panel and the in-combat panel use it so they read as the same mod
/// instead of two hand-built widgets.
/// </summary>
internal static class BotUiTheme
{
    internal static readonly Color Ink = new(0.94f, 0.93f, 0.91f);
    internal static readonly Color Muted = new(0.69f, 0.71f, 0.75f);
    internal static readonly Color Accent = new(0.84f, 0.66f, 0.29f);
    internal static readonly Color Error = new(0.94f, 0.45f, 0.40f);

    internal static bool Chinese()
    {
        try { return TranslationServer.GetLocale().StartsWith("zh", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    internal static Label Text(string text, int size, Color color)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    internal static StyleBoxFlat Card()
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.10f, 0.13f, 0.97f),
            BorderColor = new Color(Accent.R, Accent.G, Accent.B, 0.45f),
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ShadowColor = new Color(0, 0, 0, 0.45f), ShadowSize = 10,
        };
        style.SetBorderWidthAll(1);
        return style;
    }

    /// <summary>An empty styled panel; add a <see cref="Margin"/> to it, then content.</summary>
    internal static PanelContainer Panel(string name)
    {
        var panel = new PanelContainer { Name = name, MouseFilter = Control.MouseFilterEnum.Stop };
        panel.AddThemeStyleboxOverride("panel", Card());
        return panel;
    }

    internal static MarginContainer Margin(int size = 14)
    {
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", size);
        return margin;
    }

    /// <summary>Title block with the accent bar from the cover, beside a subtitle.</summary>
    internal static HBoxContainer Header(string title, string subtitle)
    {
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        header.AddChild(new ColorRect
        {
            Color = Accent,
            CustomMinimumSize = new Vector2(4, 0),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        });
        var titles = new VBoxContainer();
        titles.AddThemeConstantOverride("separation", 2);
        titles.AddChild(Text(title, 21, Ink));
        if (subtitle.Length > 0) titles.AddChild(Text(subtitle, 12, Muted));
        header.AddChild(titles);
        return header;
    }
}
