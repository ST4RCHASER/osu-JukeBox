#nullable enable

using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.UI;

/// <summary>
/// The small "★ 4.21" chip that labels one difficulty by star rating, filled with that rating's
/// <see cref="Theme.DifficultyColour"/>. Shared by the fullscreen listing's expanded difficulty rows
/// and the now-playing difficulty dropdown so both read identically.
/// </summary>
internal static class StarRatingPill
{
    /// <param name="stars">Star rating; drives both the printed number and the fill colour.</param>
    /// <param name="fontSize">Text size — the dropdown's rows are slightly larger than the listing
    /// card's, and the star glyph and paddings scale off this so the chip stays in proportion.</param>
    public static Drawable Create(double stars, float fontSize = 11) => new CircularContainer
    {
        Anchor = Anchor.CentreLeft,
        Origin = Anchor.CentreLeft,
        AutoSizeAxes = Axes.Both,
        Masking = true,
        Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = Theme.DifficultyColour(stars) },
            new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new Vector2(2, 0),
                Margin = new MarginPadding { Horizontal = fontSize * 0.55f, Vertical = 1 },
                Children = new Drawable[]
                {
                    new SpriteIcon
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Icon = FontAwesome.Solid.Star,
                        Size = new Vector2(fontSize * 0.73f),
                        // Black-on-difficulty-colour rather than white: the spectrum runs through
                        // yellow and green, which white text is unreadable against.
                        Colour = Color4.Black.Opacity(0.85f),
                    },
                    new SpriteText
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Text = stars.ToString("0.00"),
                        Font = FontUsage.Default.With(weight: "Bold", size: fontSize),
                        Colour = Color4.Black.Opacity(0.85f),
                    },
                },
            },
        },
    };
}
