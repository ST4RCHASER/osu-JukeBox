#nullable enable

using System.Collections.Generic;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Replays;
using osuTK;

namespace JukeBox.Game.Replays;

/// <summary>
/// Where a player's cursor was at a given moment, worked out from their replay frames.
///
/// <para>
/// Combine mode draws every player's cursor over one chart, and it cannot get those positions from
/// lazer's own <c>ReplayAnalysisOverlay</c>: that overlay builds its cursor, path and markers only
/// when the corresponding analysis settings are switched on, and they are off by default. Mounted
/// with them off it is an empty container — which is exactly what shipped, and why the user saw a
/// single white cursor (the playfield's own) instead of one per player.
/// </para>
///
/// <para>
/// Pure and static so the interpolation can be tested without a game host, and so the drawable that
/// uses it has nothing in it but positioning.
/// </para>
/// </summary>
public static class ReplayCursorPath
{
    /// <summary>
    /// The cursor position at <paramref name="time"/>, interpolated between the frames either side
    /// of it, or null when the replay has no positional frames at all.
    ///
    /// <para>
    /// Before the first frame and after the last, the nearest frame's position is held rather than
    /// extrapolated: a replay says nothing about where the cursor was before it started, and
    /// inventing a position would draw a cursor drifting through the intro.
    /// </para>
    /// </summary>
    public static Vector2? PositionAt(IReadOnlyList<ReplayFrame> frames, double time)
    {
        if (frames.Count == 0)
            return null;

        int low = 0;
        int high = frames.Count - 1;

        // Upper midpoint: with low = high - 1 a lower one cannot advance and this loop hangs.
        while (low < high)
        {
            int mid = (low + high + 1) / 2;

            if (frames[mid].Time <= time)
                low = mid;
            else
                high = mid - 1;
        }

        if (positionOf(frames[low]) is not { } here)
            return null;

        if (low + 1 >= frames.Count || positionOf(frames[low + 1]) is not { } next)
            return here;

        double span = frames[low + 1].Time - frames[low].Time;

        // Two frames stamped at the same moment (osu! replays do contain these) would divide by
        // zero; the earlier one stands.
        if (span <= 0)
            return here;

        float progress = (float)System.Math.Clamp((time - frames[low].Time) / span, 0, 1);

        return here + (next - here) * progress;
    }

    private static Vector2? positionOf(ReplayFrame frame)
        => frame is OsuReplayFrame osu ? osu.Position : null;
}
