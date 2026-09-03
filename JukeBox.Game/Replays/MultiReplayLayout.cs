#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace JukeBox.Game.Replays;

/// <summary>
/// How several replays of one beatmap are shown. The two answer different questions, which is why
/// both exist rather than one being a better version of the other.
/// </summary>
public enum MultiReplayMode
{
    /// <summary>
    /// One rendered chart with everyone's cursor over it, colour-coded, names and scores down the
    /// sides. Answers "how did these plays DIFFER" — the paths are directly comparable because they
    /// are drawn in the same space.
    /// </summary>
    Combine,

    /// <summary>
    /// A cell per player, each its own full render. Answers "what did each play LOOK like" — every
    /// player gets a whole playfield, at the cost of having to compare across cells by eye.
    /// </summary>
    Grid,
}

/// <summary>How many cells across and down a grid of <c>n</c> replays uses.</summary>
/// <param name="Columns">Cells across.</param>
/// <param name="Rows">Cells down.</param>
public readonly record struct GridShape(int Columns, int Rows)
{
    /// <summary>Cells the shape provides, which is at least the replay count and often one or two more.</summary>
    public int Cells => Columns * Rows;
}

/// <summary>
/// The arithmetic of watching several replays at once: how many fit, how they are arranged, and
/// whether they can share one clock at all.
///
/// <para>
/// Pure and static on purpose. Everything here is decided before a single drawable exists, so the
/// rules can be tested without a game host — and the rate rule in particular is one nobody wants to
/// discover by watching replays drift apart on screen.
/// </para>
/// </summary>
public static class MultiReplayLayout
{
    /// <summary>
    /// The most replays the grid will RENDER. Each cell is a whole gameplay renderer — its own
    /// beatmap conversion, its own hit-object pool, its own frame-stable clock — so this is a real
    /// budget rather than a display preference. Replays past it are still listed and still credited,
    /// they simply have no cell.
    ///
    /// <para>
    /// Twelve because the cost is LINEAR in cells and measurable: building the grid headless took
    /// roughly 100ms at two cells, 240ms at four, 540ms at eight and 570ms at twelve — about 55-70ms
    /// of construction per cell, before any drawing. Twelve is where that stays under a second;
    /// the reference material itself tops out at eight. (Headless says nothing about GPU cost, so
    /// this bounds the cheap half of the problem, not the expensive one.)
    /// </para>
    /// </summary>
    public const int MAX_GRID_CELLS = 12;

    /// <summary>
    /// The grid for <paramref name="count"/> replays, laid out for a WIDESCREEN box: columns are
    /// added before rows, because the player area is much wider than it is tall and square-ish
    /// arrangements waste the width. Eight becomes 4x2 — the shape the reference uses — rather than
    /// the 3x3 a naive square-root split would give.
    /// </summary>
    /// <param name="count">Replays to show. Clamped to at least one cell.</param>
    public static GridShape For(int count)
    {
        int cells = Math.Clamp(count, 1, MAX_GRID_CELLS);

        return cells switch
        {
            1 => new GridShape(1, 1),
            2 => new GridShape(2, 1),
            3 => new GridShape(3, 1),
            4 => new GridShape(2, 2),
            <= 6 => new GridShape(3, 2),
            <= 8 => new GridShape(4, 2),
            9 => new GridShape(3, 3),
            _ => new GridShape(4, 3),
        };
    }

    /// <summary>How many of <paramref name="count"/> replays actually get a cell.</summary>
    public static int RenderedCount(int count) => Math.Clamp(count, 0, MAX_GRID_CELLS);

    /// <summary>
    /// How many cells get a storyboard and video of their OWN. Past this the cells keep the map's
    /// background — one shared texture, effectively free — and drop the moving part.
    ///
    /// <para>
    /// The two halves cost very differently, which is the whole reason for a separate limit. A
    /// background is ONE decode shared by every cell. A storyboard is a decoded element tree per
    /// cell and a video is an entire decoder per cell, neither of which lazer's storyboard renderer
    /// shares between instances — so this, not the cell count, is what decides whether a big grid
    /// stays watchable.
    /// </para>
    ///
    /// <para>
    /// Four because a HEAVY storyboard has to still fit. N cell-sized storyboard layers were run in
    /// a real window with the frame limiter off, and the update thread — the limiter here, not the
    /// GPU — sustained, for a 2000-element storyboard: 1000fps at one cell, 582 at two, 215 at
    /// four, 98 at eight and 61 at twelve. A 60fps frame is 16.7ms, so at four cells a heavy
    /// storyboard costs about 4.7ms and leaves twelve for everything else; at eight it costs 10.2ms
    /// and leaves six, which the eight gameplay renderers sharing that frame will not fit in. A
    /// light (500-element) storyboard is comfortable even at twelve (273fps), but the limit has to
    /// hold for the maps that actually have big storyboards, which are the maps people want to
    /// watch this way.
    /// </para>
    ///
    /// <para>
    /// The video half is NOT measured to the same standard. N decoders were confirmed to run
    /// independently and cost real CPU (0.06 cores at one cell, 0.18 at twelve), but against a
    /// synthetic test pattern that is far cheaper to decode than a real map's video — so that
    /// figure is a floor, not a budget, and video rides this same limit rather than one of its own.
    /// </para>
    /// </summary>
    public const int STORYBOARD_CELL_LIMIT = 4;

    /// <summary>Whether a grid of <paramref name="count"/> replays draws storyboards per cell.</summary>
    public static bool StoryboardsInEveryCell(int count) => RenderedCount(count) <= STORYBOARD_CELL_LIMIT;

    /// <summary>
    /// Whether every replay in <paramref name="replays"/> was played at the same speed.
    ///
    /// <para>
    /// This is the one incompatibility a shared clock cannot paper over. Visual mods differ per
    /// replay quite happily — each cell renders under its own — but SPEED is a property of the
    /// audio track, and there is one track. A DoubleTime play and a no-mod play of the same map are
    /// different lengths, so no single clock drives both correctly. See <see cref="SharedRate"/>
    /// for what the shared clock does about it.
    /// </para>
    /// </summary>
    public static bool RatesAgree(IReadOnlyList<ReplayAttachment> replays)
        => DistinctRates(replays).Count <= 1;

    /// <summary>
    /// The distinct speeds present, in first-seen order — one entry when everything agrees. Compared
    /// with a tolerance because the rate is a product of two doubles and 1.5 does not always come
    /// back as exactly 1.5.
    /// </summary>
    public static IReadOnlyList<double> DistinctRates(IReadOnlyList<ReplayAttachment> replays)
    {
        var rates = new List<double>();

        foreach (var replay in replays)
        {
            if (!rates.Any(r => Math.Abs(r - replay.Rate) < RATE_TOLERANCE))
                rates.Add(replay.Rate);
        }

        return rates;
    }

    /// <summary>
    /// The rate everything plays at: the rate they all share when they agree, and 1.0x when they do
    /// not.
    ///
    /// <para>
    /// A mismatch used to take the FIRST replay's rate and put a "Mixed speeds — playing at Nx"
    /// chip on screen. Both are gone: picking one play's rate makes the map itself run at a speed
    /// nobody watching chose (a single DoubleTime drop turned everyone else's playback into 1.5x,
    /// decided by drop order), and the chip was a warning about that decision rather than
    /// information. 1.0x is the map's own speed, which is the neutral answer when the replays
    /// cannot agree on one.
    /// </para>
    /// </summary>
    public static double SharedRate(IReadOnlyList<ReplayAttachment> replays)
    {
        if (replays.Count == 0 || !RatesAgree(replays))
            return 1;

        return replays[0].Rate;
    }

    private const double RATE_TOLERANCE = 0.001;
}
