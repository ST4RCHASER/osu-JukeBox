#nullable enable

using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;

namespace JukeBox.Game.UI;

/// <summary>
/// One selectable option in a <see cref="BeatmapListingOverlay"/> filter row: a small rounded
/// text chip that fills accent while <see cref="Active"/> (driven by the owning row's
/// single-select logic, or toggled independently for the multi-select Extra row). Clicking fires
/// <see cref="ClickableContainer.Action"/> — the owning row decides what activation means, so the
/// chip itself never mutates <see cref="Active"/>.
/// </summary>
internal partial class FilterChip : ClickableContainer
{
    public readonly BindableBool Active = new();

    public string Text { get; }

    private readonly Box background;

    // Transforms (FadeColour) must only run after LoadComplete — see IconButton's `ready` field
    // for the same guard and reasoning.
    private bool ready;

    public FilterChip(string text)
    {
        Text = text;
        AutoSizeAxes = Axes.Both;
        Masking = true;
        CornerRadius = 10;

        Children = new Drawable[]
        {
            background = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Theme.ElevatedSurface,
            },
            new SpriteText
            {
                Text = text,
                Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                Colour = Theme.TextPrimary,
                Margin = new MarginPadding { Horizontal = 8, Vertical = 3 },
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        Active.BindValueChanged(_ => updateColour(), true);
        ready = true;
    }

    private void updateColour()
    {
        var target = Active.Value ? Theme.Accent : IsHovered ? Theme.AccentDim : Theme.ElevatedSurface;

        if (ready)
            background.FadeColour(target, Theme.HoverFadeDuration);
        else
            background.Colour = target;
    }

    protected override bool OnHover(HoverEvent e)
    {
        updateColour();
        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        updateColour();
        base.OnHoverLost(e);
    }
}
