#nullable enable

using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Graphics;
using osu.Game.Overlays.Settings;

namespace JukeBox.Game.UI;

/// <summary>
/// A concrete lazer <see cref="SettingsSection"/> (big TorusAlternate header, separator, content
/// padding). Standalone-safe: with no SettingsPanel in DI it self-selects, so the
/// section-dimming/scroll-to-section machinery is inert.
///
/// <para>
/// Shared by every panel that presents rows with lazer's real settings components — the docked
/// <see cref="SettingsOverlay"/> and the right column's <see cref="ChartPanel"/> — so the two tabs
/// are the same widget rather than two look-alikes that can drift apart.
/// </para>
/// </summary>
internal partial class LazerSection : SettingsSection
{
    private readonly LocalisableString header;
    private readonly IconUsage icon;

    public LazerSection(LocalisableString header, IconUsage icon)
    {
        this.header = header;
        this.icon = icon;
    }

    public override LocalisableString Header => header;

    public override Drawable CreateIcon() => new SpriteIcon { Icon = icon };
}

/// <summary>A concrete lazer <see cref="SettingsSubsection"/> (bold subsection header). Shared for
/// the same reasons as <see cref="LazerSection"/>.</summary>
internal partial class LazerSubsection : SettingsSubsection
{
    private readonly LocalisableString header;

    public LazerSubsection(LocalisableString header)
    {
        this.header = header;
    }

    protected override LocalisableString Header => header;
}
