#nullable enable

using System.Collections.Generic;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// The knockout death cue: when a player is eliminated their NAME drops away from the playfield and
/// fades, danser-style, at the spot they were last. In combo-break knockout their peak combo rides
/// under the name ("933x"); in imperfection knockout it is the name alone. Coloured to that player's
/// cursor, so the name leaving matches the cursor that just vanished.
/// </summary>
public partial class PlayerDeathName : CompositeDrawable
{
    public PlayerDeathName(string name, int maxCombo, Color4 colour, bool showCombo)
    {
        AutoSizeAxes = Axes.Both;
        Origin = Anchor.TopCentre;
        Alpha = 0;

        var lines = new List<Drawable>
        {
            new OsuSpriteText
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Text = name,
                Font = OsuFont.Torus.With(size: 16, weight: FontWeight.Bold),
                Colour = colour,
                Shadow = true,
            },
        };

        if (showCombo)
        {
            lines.Add(new OsuSpriteText
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Text = $"{maxCombo}x",
                Font = OsuFont.Torus.With(size: 13, weight: FontWeight.SemiBold),
                Colour = colour,
                Shadow = true,
            });
        }

        InternalChild = new FillFlowContainer
        {
            Anchor = Anchor.TopCentre,
            Origin = Anchor.TopCentre,
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 1),
            Children = lines.ToArray(),
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // danser's death drift: the name slides DOWN ~50px on an OutQuad over two seconds, fading in
        // over 200ms and out from 800ms to well past a second — gone before the slide finishes.
        this.MoveToOffset(new Vector2(0, 50), 2000, Easing.OutQuad);
        this.FadeIn(200).Then().Delay(600).FadeOut(700).Expire();
    }
}
