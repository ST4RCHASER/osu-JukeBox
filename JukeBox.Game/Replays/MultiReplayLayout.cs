#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace JukeBox.Game.Replays;

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
    /// Whether every replay in <paramref name="replays"/> was played at the same speed.
    ///
    /// <para>
    /// This is the one incompatibility a shared clock cannot paper over. Visual mods differ per
    /// replay quite happily — each cell renders under its own — but SPEED is a property of the
    /// audio track, and there is one track. A DoubleTime play and a no-mod play of the same map are
    /// different lengths, so no single clock drives both correctly, and the honest options are to
    /// refuse or to pick one and say so. We pick the FIRST replay's rate (it is the one the user
    /// dropped first, and the one whose cell reads correctly) and surface the mismatch rather than
    /// letting the others silently desync.
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

    /// <summary>The rate everything plays at: the first replay's, which is the one that stays correct.</summary>
    public static double SharedRate(IReadOnlyList<ReplayAttachment> replays)
        => replays.Count > 0 ? replays[0].Rate : 1;

    /// <summary>
    /// The warning to put on screen when rates disagree, or null when they agree and there is
    /// nothing to say.
    /// </summary>
    public static string? RateMismatchWarning(IReadOnlyList<ReplayAttachment> replays)
    {
        if (RatesAgree(replays))
            return null;

        return $"Mixed speeds — playing at {SharedRate(replays):0.##}×";
    }

    private const double RATE_TOLERANCE = 0.001;
}
