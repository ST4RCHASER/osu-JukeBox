#nullable enable

using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.UI;

/// <summary>
/// A small rounded button showing a centred <see cref="SpriteIcon"/> rather than text — used
/// wherever a button previously rendered plain Unicode glyphs (▶/⏸/⏭/✕) that the default font has
/// no coverage for and drew as "?" tofu boxes. Built on <see cref="ClickableContainer"/> (not
/// <see cref="osu.Framework.Graphics.UserInterface.BasicButton"/>, whose Text-based rendering is
/// exactly what's being avoided here) so it carries only a background <see cref="Box"/> and the
/// icon.
///
/// Follows the shared design system's interaction contract: hovering lightens the background
/// (<see cref="HoverColour"/>, a 120ms fade) and pressing scales the whole button down slightly —
/// both purely cosmetic, so every existing call site (which only sets <see cref="Icon"/>,
/// <see cref="Drawable.Size"/> and <see cref="ClickableContainer.Action"/>) keeps working
/// unmodified. <see cref="IdleColour"/>/<see cref="HoverColour"/> default to a translucent elevated
/// surface but are settable for accent-filled variants (e.g. the play/pause transport button).
/// </summary>
internal partial class IconButton : ClickableContainer
{
    private Color4 idleColour = Theme.ElevatedSurface.Opacity(0.7f);
    private Color4 hoverColour = Theme.ElevatedSurface;

    private readonly Box background;
    private readonly SpriteIcon icon;

    // Transforms (ScaleTo/FadeColour) must only run after LoadComplete — applying them from the
    // constructor or BDL risks fighting the drawable's own initial layout pass before its geometry
    // has settled. Hover/press events can't fire before load anyway, but OnMouseDown here doesn't
    // rely on that: it checks this flag explicitly.
    private bool ready;

    public IconUsage Icon
    {
        get => icon.Icon;
        set => icon.Icon = value;
    }

    public Color4 IconColour
    {
        get => icon.Colour;
        set => icon.Colour = value;
    }

    public Color4 IdleColour
    {
        get => idleColour;
        set
        {
            idleColour = value;
            if (!IsHovered)
                background.Colour = value;
        }
    }

    public Color4 HoverColour
    {
        get => hoverColour;
        set
        {
            hoverColour = value;
            if (IsHovered)
                background.Colour = value;
        }
    }

    public IconButton()
    {
        Masking = true;
        CornerRadius = Theme.CornerRadius;

        InternalChildren = new Drawable[]
        {
            background = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = idleColour,
            },
            icon = new SpriteIcon
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Size = new Vector2(16),
                Colour = Theme.TextPrimary,
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        ready = true;
    }

    protected override bool OnHover(HoverEvent e)
    {
        background.FadeColour(hoverColour, Theme.HoverFadeDuration);
        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        background.FadeColour(idleColour, Theme.HoverFadeDuration);
        base.OnHoverLost(e);
    }

    protected override bool OnMouseDown(MouseDownEvent e)
    {
        if (ready)
            this.ScaleTo(Theme.PressScale, Theme.PressScaleDuration, Easing.OutQuad);
        return base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseUpEvent e)
    {
        if (ready)
            this.ScaleTo(1f, Theme.PressScaleDuration, Easing.OutQuad);
        base.OnMouseUp(e);
    }
}
