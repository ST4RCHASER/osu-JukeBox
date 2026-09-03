#nullable enable

using System.Collections.Generic;
using JukeBox.Game.Replays;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Rulesets.Replays;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// One player's cursor on the shared playfield, in their colour, with their name riding beside it.
///
/// <para>
/// Drawn here rather than by lazer's <c>ReplayAnalysisOverlay</c>, which was the previous approach
/// and drew nothing at all: that overlay only builds its cursor, path and markers when the osu!
/// replay-analysis settings are switched on, and they are off by default. So combine mode mounted
/// N empty containers and the only cursor on screen was the playfield's own — one cursor, one
/// colour, which is exactly what the user reported.
/// </para>
///
/// <para>
/// Positioned in osu!'s own playfield coordinates (512x384) and mounted inside the ruleset's
/// <c>PlayfieldAdjustmentContainer</c>, so it inherits the playfield's transform and lands where
/// the player's cursor actually was, at any aspect or zoom.
/// </para>
/// </summary>
public partial class PlayerCursor : CompositeDrawable
{
    private readonly IReadOnlyList<ReplayFrame> frames;
    private readonly Color4 colour;

    private readonly Container body;
    private readonly OsuSpriteText nameTag;
    private readonly Circle dot;

    /// <summary>osu!'s playfield is defined in these units; everything here is placed in them.</summary>
    public static readonly Vector2 PlayfieldSize = new Vector2(512, 384);

    /// <summary>Test hook: whether this cursor currently has a position to draw at.</summary>
    internal bool HasPosition { get; private set; }

    /// <summary>Test hook: the player's colour, as drawn.</summary>
    internal Color4 Colour4 => colour;

    public PlayerCursor(string playerName, IReadOnlyList<ReplayFrame> frames, Color4 colour)
    {
        this.frames = frames;
        this.colour = colour;

        RelativeSizeAxes = Axes.None;
        Size = PlayfieldSize;

        InternalChild = body = new Container
        {
            AutoSizeAxes = Axes.Both,
            Origin = Anchor.Centre,
            Children = new Drawable[]
            {
                dot = new Circle
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Size = new Vector2(14),
                    Colour = colour,
                    // A dark rim, because a saturated dot on a busy playfield of similarly
                    // saturated hit circles is hard to pick out on its own.
                    BorderColour = Color4.Black.Opacity(0.6f),
                    BorderThickness = 3,
                    Masking = true,
                },
                nameTag = new OsuSpriteText
                {
                    // Beside the cursor rather than on it: over it, the name covers the very thing
                    // the viewer is trying to follow.
                    Anchor = Anchor.Centre,
                    Origin = Anchor.CentreLeft,
                    X = 12,
                    Text = playerName,
                    Font = OsuFont.Torus.With(size: 13, weight: FontWeight.Bold),
                    Colour = colour,
                    Shadow = true,
                },
            },
        };
    }

    protected override void Update()
    {
        base.Update();

        if (ReplayCursorPath.PositionAt(frames, Clock.CurrentTime) is not { } position)
        {
            HasPosition = false;
            body.Alpha = 0;
            return;
        }

        HasPosition = true;
        body.Alpha = 1;
        body.Position = position;
    }

    /// <summary>
    /// The combo-break cue: the name flashes red and swells for about a second, the way a tournament
    /// overlay marks a player dropping their combo. Transient by design — it says "that just
    /// happened", not "that player is finished", so it fires whether or not knockout is switched on.
    /// </summary>
    public void FlashComboBreak()
    {
        nameTag.FadeColour(Color4.Red)
               .Then().FadeColour(colour, 900, Easing.In);

        nameTag.ScaleTo(1.6f)
               .Then().ScaleTo(1, 900, Easing.OutQuint);

        // The blink is what makes it catch the eye on a busy playfield; the fade alone reads as a
        // colour change and is easy to miss.
        nameTag.FadeTo(0.2f, 80).Then().FadeTo(1, 80)
               .Then().FadeTo(0.2f, 80).Then().FadeTo(1, 80);

        dot.FlashColour(Color4.Red, 900, Easing.OutQuint);
    }
}
