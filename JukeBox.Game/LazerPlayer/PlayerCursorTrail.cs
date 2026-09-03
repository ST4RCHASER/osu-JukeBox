#nullable enable

using System.Collections.Generic;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// The fading tail behind a player's cursor, in that player's colour.
///
/// <para>
/// This is what tells the plays apart once the name tags come off the cursors. A coloured dot says
/// where someone is; a tail says where they have BEEN, which is the thing being compared when
/// several people play the same pattern — who cut the corner, who overshot, who took the long way
/// round. With a dozen cursors on one playfield the dots alone read as a swarm.
/// </para>
///
/// <para>
/// Segments are drawn in the same space as the cursor head and are recycled rather than created and
/// destroyed: with N players at 60fps, allocating a drawable per frame per player is a great deal
/// of garbage for something that is only ever a short line.
/// </para>
/// </summary>
public partial class PlayerCursorTrail : CompositeDrawable
{
    private readonly Color4 colour;
    private readonly List<Circle> segments = new List<Circle>();
    private readonly List<Vector2> positions = new List<Vector2>();

    /// <summary>
    /// How many points of history the tail keeps. Long enough to show the shape of a movement,
    /// short enough that a dozen of them do not turn the playfield into a scribble.
    /// </summary>
    private const int length = 18;

    /// <summary>Test hook: trail points currently drawn.</summary>
    internal int SegmentCount => positions.Count;

    /// <summary>
    /// Test hook: the player's colour. Read from here rather than from the drawable's own Colour,
    /// which is untouched — the tint is applied to each segment, so the container reports white.
    /// </summary>
    internal Color4 TrailColour => colour;

    public PlayerCursorTrail(Color4 colour)
    {
        this.colour = colour;
        RelativeSizeAxes = Axes.Both;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        for (int i = 0; i < length; i++)
        {
            var segment = new Circle
            {
                Origin = Anchor.Centre,
                Colour = colour,
                Alpha = 0,
            };

            segments.Add(segment);
            AddInternal(segment);
        }
    }

    /// <summary>Records where the cursor is now and redraws the tail behind it.</summary>
    public void AddPoint(Vector2 position)
    {
        positions.Add(position);

        while (positions.Count > length)
            positions.RemoveAt(0);

        for (int i = 0; i < segments.Count; i++)
        {
            if (i >= positions.Count)
            {
                segments[i].Alpha = 0;
                continue;
            }

            // Oldest at the back, faintest and smallest, so the tail reads as a direction rather
            // than a smear.
            float age = (float)(i + 1) / positions.Count;

            segments[i].Position = positions[i];
            segments[i].Size = new Vector2(2 + 5 * age);
            segments[i].Alpha = 0.45f * age;
        }
    }

    /// <summary>Drops the history — used when the cursor has no position to draw at.</summary>
    public void Clear()
    {
        positions.Clear();

        foreach (var segment in segments)
            segment.Alpha = 0;
    }
}
