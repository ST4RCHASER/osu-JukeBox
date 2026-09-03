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
/// The labelled counterpart to <see cref="IconButton"/> — a rounded flat button showing a text
/// label, optionally preceded by an icon. Shares that class's interaction contract exactly (hover
/// lightens the background over <see cref="Theme.HoverFadeDuration"/>, press scales the whole
/// button down) so the two read as one control family wherever they sit side by side, as they do
/// in the sidebar's search row and the map-ID dialog's button row.
/// </summary>
internal partial class TextButton : ClickableContainer
{
    private Color4 idleColour = Theme.ElevatedSurface.Opacity(0.7f);
    private Color4 hoverColour = Theme.ElevatedSurface;

    private readonly Box background;
    private readonly SpriteText label;

    // Same guard (and reasoning) as IconButton's: transforms must only run after LoadComplete.
    private bool ready;

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

    /// <summary>
    /// The rendered label — readable so a button can be located by what it says rather than by
    /// layout position, and settable for a button whose meaning toggles rather than being one of a
    /// pair (spectating's Start/Stop is one control in two states, not two controls).
    /// </summary>
    public string Text
    {
        get => label.Text.ToString();
        set => label.Text = value;
    }

    public TextButton(string text, IconUsage? icon = null)
    {
        Masking = true;
        CornerRadius = Theme.CornerRadius;

        var content = new FillFlowContainer
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Spacing = new Vector2(8, 0),
        };

        if (icon != null)
        {
            content.Add(new SpriteIcon
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Icon = icon.Value,
                Size = new Vector2(14),
                Colour = Theme.TextPrimary,
            });
        }

        content.Add(label = new SpriteText
        {
            Anchor = Anchor.CentreLeft,
            Origin = Anchor.CentreLeft,
            Text = text,
            Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
            Colour = Theme.TextPrimary,
        });

        InternalChildren = new Drawable[]
        {
            background = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = idleColour,
            },
            content,
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
