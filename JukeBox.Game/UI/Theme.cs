#nullable enable

using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
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

    /// <summary>Panel surface (the side columns) — ~0.92 alpha so it reads as a
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

    // ---- Beatmap status / difficulty ---------------------------------------------------------

    public static readonly Color4 StatusRanked = new Color4(0x6C, 0xCB, 0x5F, 0xFF);     // green
    public static readonly Color4 StatusLoved = new Color4(0xFF, 0x66, 0xCC, 0xFF);      // pink
    public static readonly Color4 StatusQualified = new Color4(0x4F, 0xB2, 0xFF, 0xFF);  // blue
    public static readonly Color4 StatusPending = new Color4(0xFF, 0xA5, 0x2B, 0xFF);    // orange
    public static readonly Color4 StatusGraveyard = new Color4(0x8C, 0x8C, 0x9C, 0xFF);  // grey

    /// <summary>Pill colour for a beatmap set's ranked-status string (case-insensitive; unknown
    /// statuses fall back to the graveyard grey).</summary>
    public static Color4 StatusColour(string status) => status.ToLowerInvariant() switch
    {
        "ranked" or "approved" => StatusRanked,
        "loved" => StatusLoved,
        "qualified" => StatusQualified,
        "pending" or "wip" => StatusPending,
        _ => StatusGraveyard,
    };

    // Star ratings are deliberately NOT coloured here. They use lazer's own continuous spectrum via
    // OsuColour.ForStarDifficulty (see StarRatingPill): this file used to approximate it with six
    // bands, which merged everything from 5.0 to 6.4 into one red and never reached purple at all.

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

    // ---- Animation durations / easing ------------------------------------------------------
    // Shared vocabulary for every open/close/transition animation in the app, so pace and feel
    // read as one system rather than each component picking its own numbers.

    /// <summary>Micro-interactions that need to register as instant but not abrupt — tab-switch
    /// text swaps, status-text crossfades.</summary>
    public const double DurationFast = 150;

    /// <summary>The default length for most open/close/crossfade animations (panel tab
    /// switches, card entrances, queue rows, modal pop-in/out).</summary>
    public const double DurationNormal = 250;

    /// <summary>Larger orchestrated transitions — the focus-mode layout transform, toast
    /// dismissal.</summary>
    public const double DurationSlow = 400;

    /// <summary>Standard "entering" easing — content decelerating into place.</summary>
    public const Easing EaseEnter = Easing.OutQuint;

    /// <summary>Standard "exiting" easing — content accelerating away.</summary>
    public const Easing EaseExit = Easing.InQuint;

    /// <summary>Starting/ending scale for modal and toast pop-in/pop-out (scales up to 1 on the
    /// way in, back down on the way out) — distinct from <see cref="PressScale"/>, which is a
    /// button micro-interaction, even though the two happen to share a value.</summary>
    public const float PopScale = 0.95f;

    /// <summary>Subtle drop shadow used behind the side columns and the boxed player panel.</summary>
    public static EdgeEffectParameters PanelShadow => new EdgeEffectParameters
    {
        Type = EdgeEffectType.Shadow,
        Colour = Color4.Black.Opacity(0.4f),
        Radius = 12,
    };
}
