#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace JukeBox.Game.Replays;

/// <summary>
/// One watched player's current play, as the renderer needs it: which map, which difficulty, and
/// the replay that drives it.
/// </summary>
/// <param name="SetDirectory">The cached set's folder — where the pane's audio and background live.</param>
/// <param name="OsuFile">Absolute path of the exact difficulty played.</param>
/// <param name="Replay">The decoded replay driving this pane.</param>
/// <param name="DisplayName">Who to credit, already formatted.</param>
public readonly record struct SpectateEntry(string SetDirectory, string OsuFile, ReplayAttachment Replay, string DisplayName);

/// <summary>
/// How to show several people's plays at once, decided before any drawable exists.
///
/// <para>
/// Pure and static for the same reason <see cref="MultiReplayLayout"/> is: these rules decide which
/// renderer runs and how loud each pane is, and neither is something anyone wants to discover by
/// watching the wrong thing happen on screen. It is also the whole of what the hosting screen needs
/// to ask — <see cref="AllOnOneMap"/> is the choice between the existing same-map renderers and
/// <c>IndependentReplayPanes</c>.
/// </para>
/// </summary>
public static class SpectatePanePlan
{
    /// <summary>
    /// The most independent panes rendered at once.
    ///
    /// <para>
    /// Lower than <see cref="MultiReplayLayout.MAX_GRID_CELLS"/> (12) on purpose, because a pane
    /// costs strictly more than a grid cell. The grid's cells share one map: one background texture,
    /// one audio track, one storyboard. Independent panes share nothing — each decodes its own
    /// beatmap, holds its own track store and its own audio voice, and runs its own storyboard. Four
    /// is also where <see cref="MultiReplayLayout.STORYBOARD_CELL_LIMIT"/> already caps storyboards
    /// for the shared case, so it is the point past which cells stop being full renders anyway.
    /// </para>
    ///
    /// <para>
    /// It also happens to be what the replay-download budget can feed: osu!'s API allows ten replay
    /// downloads a minute, so four panes can each be refreshed comfortably within it while a larger
    /// wall could not be kept current.
    /// </para>
    /// </summary>
    public const int MAX_PANES = 4;

    /// <summary>
    /// Whether every entry is playing the SAME difficulty — the question that decides which
    /// renderer runs.
    ///
    /// <para>
    /// True means the existing <see cref="MultiReplayMode"/> renderers apply unchanged: one map,
    /// one clock, everyone synced, which is the comparison they were built for. False means the
    /// plays cannot share a clock at all and each needs its own — see <c>IndependentReplayPanes</c>.
    /// </para>
    ///
    /// <para>
    /// Compared on the .osu PATH rather than the set: two people on different difficulties of the
    /// same mapset are playing different charts of different lengths, which is the same problem as
    /// two different maps and not something one clock can drive.
    /// </para>
    /// </summary>
    public static bool AllOnOneMap(IReadOnlyList<SpectateEntry> entries)
        => entries.Count <= 1
           || entries.All(e => string.Equals(e.OsuFile, entries[0].OsuFile, StringComparison.Ordinal));

    /// <summary>The entries that actually get a pane, in order, capped at <see cref="MAX_PANES"/>.</summary>
    public static IReadOnlyList<SpectateEntry> Rendered(IReadOnlyList<SpectateEntry> entries)
        => entries.Count <= MAX_PANES ? entries : entries.Take(MAX_PANES).ToList();

    /// <summary>
    /// How loud each rendered pane starts: the first at full, the rest silent.
    ///
    /// <para>
    /// Not a preference but the only workable default. Every pane has its own audio track, and four
    /// unrelated songs at once is noise rather than a feature — so one plays and the others are
    /// there to be unmuted deliberately. The FIRST is chosen rather than a "best" one because
    /// position is something the user can see and predict; picking by score or rank would move the
    /// sound around as plays land.
    /// </para>
    /// </summary>
    public static IReadOnlyList<double> InitialVolumes(int paneCount)
    {
        var volumes = new double[Math.Max(0, paneCount)];

        if (volumes.Length > 0)
            volumes[0] = 1;

        return volumes;
    }

    /// <summary>
    /// The grid the panes are laid out in — the SAME arithmetic the shared-map grid uses, so a
    /// four-up of different maps and a four-up of one map look like the same product rather than
    /// two features that happen to coexist.
    /// </summary>
    public static GridShape Shape(int paneCount) => MultiReplayLayout.For(Math.Min(paneCount, MAX_PANES));

    /// <summary>
    /// What each pane plays at. Every entry keeps its OWN replay's rate, and unlike the shared-map
    /// case there is nothing to reconcile.
    ///
    /// <para>
    /// <see cref="MultiReplayLayout.SharedRate"/> exists because the same-map renderers have ONE
    /// audio track between them: a DoubleTime play and a no-mod play of the same chart cannot both
    /// be driven correctly, so that path has to collapse them to a single rate and at least one
    /// player ends up at the wrong speed. Independent panes have one track EACH, so the compromise
    /// simply does not arise here — mixed speeds are the normal case rather than something to
    /// reconcile.
    /// </para>
    /// </summary>
    public static IReadOnlyList<double> Rates(IReadOnlyList<SpectateEntry> entries)
        => entries.Select(e => e.Replay.Rate).ToList();
}
