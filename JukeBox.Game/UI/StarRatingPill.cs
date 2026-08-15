#nullable enable

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics;
using osuTK;

namespace JukeBox.Game.UI;

/// <summary>
/// The small "★ 4.21" chip that labels one difficulty by star rating. Shared by the fullscreen
/// listing's expanded difficulty rows and the now-playing difficulty dropdown so both read
/// identically.
///
/// <para>
/// Fill and text colour both come from lazer's own <see cref="OsuColour.ForStarDifficulty"/> /
/// <see cref="OsuColour.ForStarDifficultyText"/> — a CONTINUOUS gradient sampled at the exact
/// rating, which is why this is a drawable resolving <see cref="OsuColour"/> rather than a static
/// factory. The app previously used a handful of hand-picked bands, which collapsed every rating
/// from 5.0 to 6.4 into one indistinguishable red and never reached the purple end at all: a 5.10
/// and a 6.22 looked the same, and neither looked like the game.
/// </para>
///
/// <para>
/// The text rule is lazer's too, and is not decoration: past 6.5 the fill is dark purple heading to
/// black, where black text would be unreadable — lazer switches to a light orange, then to its own
/// gradient past 9.
/// </para>
/// </summary>
internal partial class StarRatingPill : CircularContainer
{
    private readonly double stars;
    private readonly float fontSize;

    /// <param name="stars">Star rating; drives the printed number and both colours.</param>
    /// <param name="fontSize">Text size — the dropdown's rows are slightly smaller than the listing
    /// card's, and the star glyph and paddings scale off this so the chip stays in proportion.</param>
    public StarRatingPill(double stars, float fontSize = 11)
    {
        this.stars = stars;
        this.fontSize = fontSize;

        Anchor = Anchor.CentreLeft;
        Origin = Anchor.CentreLeft;
        AutoSizeAxes = Axes.Both;
        Masking = true;
    }

    [BackgroundDependencyLoader]
    private void load(OsuColour colours)
    {
        var textColour = colours.ForStarDifficultyText(stars);

        Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = colours.ForStarDifficulty(stars) },
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
                        Colour = textColour,
                    },
                    new SpriteText
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Text = stars.ToString("0.00"),
                        Font = FontUsage.Default.With(weight: "Bold", size: fontSize),
                        Colour = textColour,
                    },
                },
            },
        };
    }
}
