#nullable enable

using System;
using System.Collections.Generic;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Utils;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// The knockout death cue: when a player is eliminated their NAME drops away from the playfield and
/// fades, danser-style, at the spot they were last. In combo-break knockout the combo they broke on
/// rides under the name ("933x"); in imperfection knockout it is the name alone. Coloured to that
/// player's cursor.
///
/// <para>
/// Driven entirely from the timeline by the combine layer — not self-animated — so it is
/// SEEK-CORRECT: at any playhead it shows the exact state its age implies (mid-fall if seeking into
/// the window, gone if seeking past it, absent if before), rather than a transform that only plays
/// forward once. The combine sets <see cref="SetProgress"/> each frame against a fixed base position.
/// </para>
/// </summary>
public partial class PlayerDeathName : CompositeDrawable
{
    /// <summary>How long the whole fall lasts. The name is gone by the end.</summary>
    public const double Duration = 2000;

    private const float fall_distance = 50;

    private Vector2 basePosition;
    private readonly float jitterX;
    private readonly float jitterY;

    public PlayerDeathName(string name, int combo, Color4 colour, bool showCombo)
    {
        AutoSizeAxes = Axes.Both;
        Origin = Anchor.TopCentre;

        // A few pixels of scatter so two players dying on the same object do not stack into one
        // illegible smear — danser jitters its death names the same way.
        jitterX = RNG.NextSingle(-6, 6);
        jitterY = RNG.NextSingle(-4, 4);

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
                Text = $"{combo}x",
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

    /// <summary>The spot the player was knocked out at, in the combine's local space. The name falls
    /// downward from here.</summary>
    public Vector2 BasePosition
    {
        set => basePosition = value;
    }

    /// <summary>
    /// Places the name for an age of <paramref name="elapsed"/> ms since the knockout: it slides down
    /// on an OutQuad and fades in over the first 200ms, then out over the last 700ms. Computed fresh
    /// every frame so seeking lands it exactly where its age says rather than resuming a transform.
    /// </summary>
    public void SetProgress(double elapsed)
    {
        double t = Math.Clamp(elapsed / Duration, 0, 1);
        float fall = fall_distance * (float)Interpolation.ApplyEasing(Easing.OutQuad, t);

        Position = basePosition + new Vector2(jitterX, jitterY + fall);

        // In over 0-200ms, hold, out over the last 700ms.
        float fadeIn = (float)Math.Clamp(elapsed / 200.0, 0, 1);
        float fadeOut = (float)Math.Clamp((Duration - elapsed) / 700.0, 0, 1);
        Alpha = Math.Min(fadeIn, fadeOut);
    }
}
