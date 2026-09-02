#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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

    /// <summary>
    /// Absolute path of the dropped .osr itself. Kept so a second process on the same machine (the
    /// detached viewer window — see <see cref="Detach.ViewerSyncState.ReplayOsrPath"/>) can decode
    /// the same replay for itself instead of receiving megabytes of frames over the sync pipe.
    /// Empty for an attachment built by a test that never had a file.
    /// </summary>
    public string SourcePath { get; init; } = string.Empty;

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
    /// The play's speed change applied as TEMPO — faster or slower with pitch preserved. This is
    /// DoubleTime's and HalfTime's half of the story; 1 when neither is present. See
    /// <see cref="ReplayMods.TrackAdjustmentsFor"/> for why the two are tracked separately.
    /// </summary>
    public double RateTempo { get; init; } = 1;

    /// <summary>
    /// The play's speed change applied as FREQUENCY — faster or slower with pitch shifted with it.
    /// This is Nightcore's and Daycore's half; 1 when neither is present.
    /// </summary>
    public double RateFrequency { get; init; } = 1;

    /// <summary>
    /// Effective playback speed, i.e. both halves combined — 1.5 under DT or NC, 0.75 under HT or
    /// DC, 1 otherwise. Applied to actual PLAYBACK rather than to the chart, so the music changes
    /// speed with the gameplay and the two stay in sync, which is what the rate mods do in osu!
    /// itself. Display and logging only; the adjustments themselves are the two above.
    /// </summary>
    public double Rate => RateTempo * RateFrequency;

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
    /// <summary>
    /// Every replay held for a difficulty, newest registration last. A LIST rather than one
    /// attachment because several people's replays of the same difficulty are watched together —
    /// dropping five .osr for one map is one viewing session, not five that overwrite each other,
    /// which is what a single-valued entry did.
    /// </summary>
    // Written from the import's threadpool continuation, read from the async drawable-load thread.
    private readonly ConcurrentDictionary<string, ImmutableList<ReplayAttachment>> byOsuFile = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds <paramref name="attachment"/> to its own difficulty, keeping any already there. A no-op
    /// for an attachment whose difficulty never resolved.
    ///
    /// <para>
    /// Re-registering the same SOURCE FILE replaces its entry rather than doubling it, so importing
    /// the same .osr twice (a re-drop, or a launch argument repeated) leaves one copy — otherwise a
    /// duplicate would show up as a second player rendering identical cursors.
    /// </para>
    /// </summary>
    public void Register(ReplayAttachment attachment)
    {
        if (attachment.OsuFile == null)
            return;

        byOsuFile.AddOrUpdate(attachment.OsuFile,
            _ => ImmutableList.Create(attachment),
            (_, existing) =>
            {
                int same = attachment.SourcePath.Length == 0
                    ? -1
                    : existing.FindIndex(a => string.Equals(a.SourcePath, attachment.SourcePath, StringComparison.Ordinal));

                return same >= 0 ? existing.SetItem(same, attachment) : existing.Add(attachment);
            });
    }

    /// <summary>
    /// The FIRST replay for <paramref name="osuFile"/>, or null when that difficulty has none. The
    /// single-replay view of the store, which is what everything rendering one chart still wants.
    /// </summary>
    public ReplayAttachment? ForOsuFile(string? osuFile)
        => AllForOsuFile(osuFile).FirstOrDefault();

    /// <summary>
    /// Every replay for <paramref name="osuFile"/>, in registration order — empty when there are
    /// none. Registration order is import order, which is the order the user dropped them.
    /// </summary>
    public IReadOnlyList<ReplayAttachment> AllForOsuFile(string? osuFile)
        => osuFile != null && byOsuFile.TryGetValue(osuFile, out var attachments)
            ? attachments
            : ImmutableList<ReplayAttachment>.Empty;
}
