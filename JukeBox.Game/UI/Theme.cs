#nullable enable

using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics.Effects;
using osuTK.Graphics;

namespace JukeBox.Game.UI;

/// <summary>
/// Central design-system constants (colours, spacing, radii, type sizes and animation durations)
/// shared by every drawable in this namespace, so the whole interface reads as one consistent
/// surface rather than a pile of ad-hoc magic numbers scattered across components.
/// </summary>
internal static class Theme
{
    // ---- Colours ---------------------------------------------------------------------------

    /// <summary>Outermost background scrim, sitting behind the storyboard/video visuals.</summary>
    public static readonly Color4 Background = new Color4(0x14, 0x14, 0x1B, 0xFF);

    /// <summary>Panel surface (left column, bottom bar) — ~0.92 alpha so it reads as a
    /// translucent layer over the visuals rather than a fully opaque backdrop.</summary>
    public static readonly Color4 PanelSurface = new Color4(0x1E, 0x1E, 0x28, 235);

    /// <summary>Elevated surface for controls that sit "above" a panel — text boxes, thumbnails,
    /// hovered rows.</summary>
    public static readonly Color4 ElevatedSurface = new Color4(0x28, 0x28, 0x34, 0xFF);

    public static readonly Color4 Accent = new Color4(0xFF, 0x66, 0xAA, 0xFF);
    public static readonly Color4 AccentDim = new Color4(0xB2, 0x4A, 0x78, 0xFF);

    public static readonly Color4 TextPrimary = Color4.White;
    public static readonly Color4 TextSecondary = new Color4(0xB8, 0xB8, 0xC8, 0xFF);
    public static readonly Color4 TextTertiary = new Color4(0x7A, 0x7A, 0x8C, 0xFF);

    public static readonly Color4 Error = new Color4(0xFF, 0x5C, 0x5C, 0xFF);

    /// <summary>Chart-renderer combo colours: the accent plus three accent-adjacent hues, cycled
    /// per combo so consecutive combos read as distinct groups.</summary>
    public static readonly Color4[] ComboColours =
    {
        Accent,                                    // pink (accent)
        new Color4(0x66, 0xB8, 0xFF, 0xFF),        // sky blue
        new Color4(0xFF, 0xC0, 0x66, 0xFF),        // warm gold
        new Color4(0x7E, 0xE0, 0xA8, 0xFF),        // mint
    };

    /// <summary>Dim scrim shown behind centred modal overlays (settings/map-id) and the fullscreen
    /// search dropdown.</summary>
    public static readonly Color4 ModalScrim = Color4.Black.Opacity(0.7f);

    // ---- Shape / spacing --------------------------------------------------------------------

    public const float CornerRadius = 8;
    public const float PanelPadding = 16;
    public const float RowSpacing = 8;
    public const float SectionSpacing = 12;

    // ---- Type scale --------------------------------------------------------------------------

    public const float HeaderTextSize = 20;
    public const float RowTitleTextSize = 16;
    public const float RowSecondaryTextSize = 13;
    public const float CaptionTextSize = 12;

    // ---- Interaction durations -----------------------------------------------------------------

    public const double HoverFadeDuration = 120;
    public const double PressScaleDuration = 80;
    public const float PressScale = 0.95f;

    /// <summary>Subtle drop shadow used behind the left panel and the bottom playback bar.</summary>
    public static EdgeEffectParameters PanelShadow => new EdgeEffectParameters
    {
        Type = EdgeEffectType.Shadow,
        Colour = Color4.Black.Opacity(0.4f),
        Radius = 12,
    };
}
