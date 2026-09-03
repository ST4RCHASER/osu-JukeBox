#nullable enable

using System;

namespace JukeBox.Game.Replays;

/// <summary>How the scoreboard is sized for a given number of players in a given amount of height.</summary>
/// <param name="RowHeight">Height of one row.</param>
/// <param name="FontSize">Text size within it.</param>
/// <param name="DotSize">Diameter of the colour dot tying a row to its cursor.</param>
/// <param name="Width">Board width.</param>
/// <param name="VisibleRows">Rows actually drawn — fewer than the player count only when even the
/// smallest row will not fit.</param>
/// <param name="ShowPerformance">Whether the pp column is drawn.</param>
public readonly record struct RailMetrics(
    float RowHeight,
    float FontSize,
    float DotSize,
    float Width,
    int VisibleRows,
    bool ShowPerformance);

/// <summary>
/// The arithmetic of fitting N players on screen at once.
///
/// <para>
/// Everything derives from the height actually available rather than from constants, because the
/// board previously sized itself to its CONTENT: 47 players at a fixed row height came to more than
/// a thousand pixels of board, which ran off the bottom of the player box and over the app behind
/// it. A scoreboard that does not fit on screen is not a scoreboard.
/// </para>
///
/// <para>
/// Pure and static so the scaling can be checked at any count and any height without a window —
/// the interesting cases are the extremes, and those are the ones hardest to produce by hand.
/// </para>
/// </summary>
public static class RailDensity
{
    /// <summary>The row height at small player counts, and the size the board has always used.</summary>
    public const float MAX_ROW_HEIGHT = 22;

    /// <summary>
    /// The smallest row worth drawing. Below this the text stops being readable, so shrinking
    /// further buys nothing but the illusion of having fitted everyone in.
    /// </summary>
    public const float MIN_ROW_HEIGHT = 10;

    /// <summary>
    /// Rows tighter than this drop the pp column — the widest of the left-hand fields and the least
    /// load-bearing, so it is the one to lose first when there is no room for all five.
    /// </summary>
    public const float PERFORMANCE_ROW_HEIGHT = 15;

    /// <summary>The board never takes the full height; this leaves the edge of the scene visible.</summary>
    private const float height_fraction = 0.94f;

    /// <summary>
    /// How the board should be drawn for <paramref name="playerCount"/> players in
    /// <paramref name="availableHeight"/> pixels.
    /// </summary>
    public static RailMetrics For(int playerCount, float availableHeight)
    {
        if (playerCount <= 0)
            return new RailMetrics(MAX_ROW_HEIGHT, 12, 8, 300, 0, true);

        float usable = Math.Max(availableHeight * height_fraction, MIN_ROW_HEIGHT);
        float rowHeight = Math.Clamp(usable / playerCount, MIN_ROW_HEIGHT, MAX_ROW_HEIGHT);

        // Past the point where even the smallest row will not fit for everyone, show as many as do
        // and say how many are missing. Silently cutting the list would hide players; drawing them
        // anyway is the overflow this exists to prevent.
        int visible = Math.Min(playerCount, Math.Max((int)(usable / rowHeight), 1));

        float fontSize = Math.Clamp(rowHeight * 0.55f, 7, 12);

        return new RailMetrics(
            rowHeight,
            fontSize,
            fontSize * 0.7f,
            Math.Clamp(fontSize * 26, 200, 340),
            visible,
            rowHeight >= PERFORMANCE_ROW_HEIGHT);
    }

    /// <summary>How many players are not drawn at all, for the "+N more" line.</summary>
    public static int Hidden(int playerCount, float availableHeight)
        => Math.Max(0, playerCount - For(playerCount, availableHeight).VisibleRows);
}
