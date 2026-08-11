#nullable enable

using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.UI;

/// <summary>
/// A small square button showing a centred <see cref="SpriteIcon"/> rather than text — used
/// wherever a button previously rendered plain Unicode glyphs (▶/⏸/⏭/✕) that the default font has
/// no coverage for and drew as "?" tofu boxes. Built on <see cref="ClickableContainer"/> (not
/// <see cref="osu.Framework.Graphics.UserInterface.BasicButton"/>, whose Text-based rendering is
/// exactly what's being avoided here) so it carries only a background <see cref="Box"/> and the
/// icon.
/// </summary>
internal partial class IconButton : ClickableContainer
{
    private static readonly Color4 idle_colour = new Color4(60, 60, 60, 255);
    private static readonly Color4 hover_colour = new Color4(90, 90, 90, 255);

    private readonly Box background;
    private readonly SpriteIcon icon;

    public IconUsage Icon
    {
        get => icon.Icon;
        set => icon.Icon = value;
    }

    public IconButton()
    {
        InternalChildren = new Drawable[]
        {
            background = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = idle_colour,
            },
            icon = new SpriteIcon
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Size = new Vector2(16),
            },
        };
    }

    protected override bool OnHover(HoverEvent e)
    {
        background.Colour = hover_colour;
        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        background.Colour = idle_colour;
        base.OnHoverLost(e);
    }
}
