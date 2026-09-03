#nullable enable

using System.Collections.Generic;
using JukeBox.Game.Replays;
using JukeBox.Game.UI;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Utils;
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
    private Color4 colour;

    private readonly Container content;
    private readonly Container body;
    private readonly Circle dot;

    /// <summary>osu!'s playfield is defined in these units; replay frames are recorded in them.</summary>
    public static readonly Vector2 PlayfieldSize = new Vector2(512, 384);

    /// <summary>
    /// The drawable whose local space replay coordinates mean something in — the playfield itself.
    ///
    /// <para>
    /// Set when the cursor is mounted, and the fix for cursors being drawn in the wrong place
    /// entirely. Sitting in the playfield's ADJUSTMENT container is not the same as sitting in the
    /// playfield: measured, the two share a top-left and both report a 512x384 draw size, but the
    /// playfield covered 819x614 of screen where the adjustment container covered 1802x1352. So a
    /// cursor placed at raw replay coordinates was drawn at more than twice the scale the objects
    /// were, which puts anything away from the top-left corner progressively further out — and
    /// anything near the bottom-right off the playfield altogether.
    /// </para>
    ///
    /// <para>
    /// Mapping through the playfield's own transform rather than guessing a scale means this stays
    /// correct if the zoom setting, the aspect, or lazer's own adjustment maths ever change.
    /// </para>
    /// </summary>
    internal Drawable? PositionSpace { get; set; }

    /// <summary>Test hook: whether this cursor currently has a position to draw at.</summary>
    internal bool HasPosition { get; private set; }

    /// <summary>Where the cursor dot is on screen right now — used to place the knockout death name
    /// at the spot the player was last, since the cursor itself vanishes the instant they are out.</summary>
    internal Vector2 CursorScreenPosition => body.ScreenSpaceDrawQuad.Centre;

    /// <summary>Test hook: the player's colour, as drawn.</summary>
    internal Color4 Colour4 => colour;

    /// <summary>Re-tints the cursor and its trail live, for a per-player colour change. The break
    /// marker stays red — it is a state cue, not part of the player's identity colour.</summary>
    public void SetColour(Color4 value)
    {
        colour = value;
        dot.Colour = value;
        trail.SetColour(value);
    }

    public PlayerCursor(string playerName, IReadOnlyList<ReplayFrame> frames, Color4 colour)
    {
        this.frames = frames;
        this.colour = colour;

        // Fills whatever it is mounted in; the actual placement goes through PositionSpace, so this
        // container's own size is only there to give the cursor somewhere to live.
        RelativeSizeAxes = Axes.Both;

        this.playerName = playerName;

        // Everything the cursor draws lives inside one content container whose alpha is the FOCUS
        // channel: hovering this player's rail row fades every OTHER player's content to a whisper
        // (see SetFocusAlpha) while the eliminated/alive state stays on this class's own alpha, so
        // the two dim for independent reasons and multiply rather than fight.
        InternalChild = content = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Children = new Drawable[]
            {
                // The trail is drawn UNDER the cursor and in the playfield's own space, so its
                // segments land where the cursor has been rather than following the head around.
                trail = new PlayerCursorTrail(colour),

                body = new Container
                {
                    AutoSizeAxes = Axes.Both,
                    Origin = Anchor.Centre,
                    Child = dot = new Circle
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Size = new Vector2(11),
                        Colour = colour,
                        // A dark rim, because a saturated dot on a busy playfield of similarly
                        // saturated hit circles is hard to pick out on its own.
                        BorderColour = Color4.Black.Opacity(0.6f),
                        BorderThickness = 2.5f,
                        Masking = true,
                    },
                },
            },
        };
    }

    /// <summary>
    /// The focus channel: 1 for a normally-drawn cursor, a whisper (0.1) for every player who is
    /// not the one whose rail row is being hovered. Fades over <see cref="Theme.DurationFast"/> so
    /// picking a row out of a swarm of 47 cursors reads as the rest stepping back rather than
    /// snapping off. Applied to the content container, not this drawable's own alpha, so it survives
    /// the per-frame alive/eliminated alpha the combine layer writes.
    /// </summary>
    public void SetFocusAlpha(float target) => content.FadeTo(target, Theme.DurationFast, Easing.OutQuint);

    /// <summary>Test hook: the focus channel's current value, which the alive/eliminated alpha
    /// multiplies over rather than overwrites.</summary>
    internal float FocusAlpha => content.Alpha;

    private readonly string playerName;
    private readonly PlayerCursorTrail trail;

    protected override void Update()
    {
        base.Update();

        if (ReplayCursorPath.PositionAt(frames, Clock.CurrentTime) is not { } position)
        {
            HasPosition = false;
            body.Alpha = 0;
            trail.Clear();
            return;
        }

        HasPosition = true;
        body.Alpha = 1;

        // Through the playfield's own transform, not as a raw coordinate: see PositionSpace.
        var local = PositionSpace is { } space
            ? ToLocalSpace(space.ToScreenSpace(position))
            : position;

        body.Position = local;
        trail.AddPoint(local);
    }

    /// <summary>Where this player's cursor was at <paramref name="time"/>, in SCREEN space — for
    /// placing the death name at the exact spot they were knocked out, a fixed past position rather
    /// than wherever the (now vanished) cursor is live. Null when the replay had no frame there.</summary>
    internal Vector2? ScreenPositionAt(double time)
    {
        if (ReplayCursorPath.PositionAt(frames, time) is not { } position)
            return null;

        return PositionSpace is { } space ? space.ToScreenSpace(position) : ToScreenSpace(position);
    }

    /// <summary>
    /// The combo-break cue on the playfield: the player's NAME appears at the point where they
    /// dropped it, in red, and fades.
    ///
    /// <para>
    /// The name is not on the cursor any more — with a dozen or more players the tags overlapped
    /// into an unreadable pile, and the rail is where names belong. It comes back for a moment at
    /// the one instant it answers a question the rail cannot: not "who is that", but "who just
    /// missed, and where". It is left behind at the point of the miss rather than following the
    /// cursor, because the interesting place is where the break happened.
    /// </para>
    /// </summary>
    public void FlashComboBreak()
    {
        dot.FlashColour(Color4.Red, 900, Easing.OutQuint);

        // Jittered a few pixels off the miss point so two players who break on the same object do
        // not drop identical names on top of each other into one illegible smear — danser scatters
        // its death bubbles the same way.
        var start = body.Position + new Vector2(RNG.NextSingle(-5, 5), RNG.NextSingle(-5, 5));

        var marker = new OsuSpriteText
        {
            Origin = Anchor.Centre,
            Position = start,
            Text = playerName,
            Font = OsuFont.Torus.With(size: 14, weight: FontWeight.Bold),
            Colour = Color4.Red,
            Shadow = true,
            Alpha = 0,
        };

        content.Add(marker);

        // danser's death bubble: it SLIDES down ~50px over two seconds on an OutQuad, rather than
        // popping and fading in place. The slide is what reads as "dropped" — a name settling
        // downward off the playfield instead of a label blinking where the cursor happened to be.
        marker.MoveToY(start.Y + 50, 2000, Easing.OutQuad);

        // Fade in over 200ms, hold, then out from 800ms to 1200ms — the bubble is gone well before
        // the slide finishes, so its last stretch of travel is spent invisible, exactly as danser
        // times it.
        marker.FadeIn(200).Then().Delay(600).FadeOut(400).Expire();
    }
}
