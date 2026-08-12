#nullable enable

using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osuTK;

namespace JukeBox.Game.UI;

/// <summary>
/// The Split layout's compact search entry point: styled like an idle <see cref="AccentTextBox"/>
/// (rounded elevated surface, magnifier icon, tertiary placeholder text) but it is a button, not a
/// text box — clicking it opens the full <see cref="BeatmapListingOverlay"/> (via
/// <see cref="ClickableContainer.Action"/>, wired by MainScreen). It deliberately never takes
/// keyboard focus so the screen-level "type anywhere to search" behaviour keeps working: a typed
/// character reaches MainScreen and opens the listing seeded with that char instead of being
/// swallowed by a focused local text box.
/// </summary>
internal partial class CompactSearchButton : ClickableContainer
{
    private Box background = null!;

    // Transforms (FadeColour) must only run after LoadComplete — see IconButton's `ready` field
    // for the same guard and reasoning.
    private bool ready;

    public CompactSearchButton()
    {
        Masking = true;
        CornerRadius = Theme.CornerRadius;

        Children = new Drawable[]
        {
            background = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Theme.ElevatedSurface,
            },
            new FillFlowContainer
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Padding = new MarginPadding { Left = 10 },
                Spacing = new Vector2(8, 0),
                Children = new Drawable[]
                {
                    new SpriteIcon
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Icon = FontAwesome.Solid.Search,
                        Size = new Vector2(14),
                        Colour = Theme.TextTertiary,
                    },
                    new SpriteText
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Text = "Search beatmaps…",
                        Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                        Colour = Theme.TextTertiary,
                    },
                },
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
        if (ready)
            background.FadeColour(Theme.ElevatedSurface.Lighten(0.15f), Theme.HoverFadeDuration);
        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        if (ready)
            background.FadeColour(Theme.ElevatedSurface, Theme.HoverFadeDuration);
        base.OnHoverLost(e);
    }
}
