#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using osu.Game.Scoring;

namespace JukeBox.Game.Replays;

/// <summary>
/// A replay the user dropped onto the window, paired with the beatmap it was resolved against.
/// Carried on <see cref="Online.BeatmapSetInfo.Replay"/> so it travels with the set through the
/// queue and the now-playing bindable (which is what puts "Played by X" on the queue card and in
/// the playback panel), and registered in <see cref="ReplayStore"/> so the rendering side can find
/// it by difficulty without depending on that travel order.
/// </summary>
public class ReplayAttachment
{
    /// <summary>The player's name as the replay recorded it; may be empty.</summary>
    public string PlayerName { get; init; } = string.Empty;

    /// <summary>MD5 of the exact .osu the replay was played on.</summary>
    public string BeatmapMd5 { get; init; } = string.Empty;

    /// <summary>
    /// Absolute path of the cached .osu whose MD5 matches <see cref="BeatmapMd5"/> — the EXACT
    /// difficulty played, which is generally not the set's default one. Null when the resolved set
    /// turned out not to contain a matching difficulty (a mirror serving a re-uploaded/edited set);
    /// playback then falls back to normal autoplay on the default difficulty.
    /// </summary>
    public string? OsuFile { get; init; }

    /// <summary>
    /// The fully decoded score, replay frames included — what
    /// <c>DrawableRuleset.SetReplayScore</c> is given so gameplay follows the real play instead of
    /// autoplay. Null when the decode failed (logged), in which case playback degrades to autoplay
    /// while the "Played by X" credit still shows.
    /// </summary>
    public Score? Score { get; init; }

    /// <summary>
    /// The play's mods as short names ("HD", "HR", "DT"), in osu!'s own display order. Empty for a
    /// no-mod play, or when the replay never decoded. Shown on the queue card and under the
    /// "Played by" line in the playback panel.
    /// </summary>
    public IReadOnlyList<string> ModAcronyms { get; init; } = Array.Empty<string>();

    /// <summary>
    /// How much faster the play ran than the beatmap's own timing — 1.5 under DT/NC, 0.75 under
    /// HT/DC, 1 otherwise. Applied to actual PLAYBACK (see <see cref="Playback.PlaybackController.ReplayRate"/>)
    /// rather than to the chart, so the music speeds up with the gameplay and the two stay in sync,
    /// which is what the rate mods do in osu! itself.
    /// </summary>
    public double Rate { get; init; } = 1;

    /// <summary>Whether <see cref="Rate"/> should shift pitch too — see <see cref="ReplayMods.ShiftsPitch"/>.</summary>
    public bool RateShiftsPitch { get; init; }

    /// <summary>When the play happened, per the replay header.</summary>
    public DateTimeOffset PlayedAt { get; init; }
}

/// <summary>
/// Session-lifetime registry of dropped replays, keyed by the difficulty they belong to.
///
/// <para>
/// Keying on the .osu path rather than tracking "the current replay" is what makes this
/// order-independent: the rendering stack (<see cref="Screens.BeatmapVisuals"/>) is built from a
/// difficulty and simply asks whether there is a replay for it, so it doesn't matter whether the
/// import, the enqueue or the playback swap happened first — and switching difficulty away from
/// the replay's own falls back to autoplay, then back again restores the replay, for free.
/// </para>
/// </summary>
public class ReplayStore
{
    // Written from the import's threadpool continuation, read from the async drawable-load thread.
    private readonly ConcurrentDictionary<string, ReplayAttachment> byOsuFile = new(StringComparer.Ordinal);

    /// <summary>Registers <paramref name="attachment"/> against its own difficulty. A no-op for an
    /// attachment whose difficulty never resolved.</summary>
    public void Register(ReplayAttachment attachment)
    {
        if (attachment.OsuFile != null)
            byOsuFile[attachment.OsuFile] = attachment;
    }

    /// <summary>The replay for <paramref name="osuFile"/>, or null when that difficulty has none.</summary>
    public ReplayAttachment? ForOsuFile(string? osuFile)
        => osuFile != null && byOsuFile.TryGetValue(osuFile, out var attachment) ? attachment : null;
}
