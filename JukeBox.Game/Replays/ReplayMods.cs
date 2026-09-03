#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Audio;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;
using osu.Game.Utils;

namespace JukeBox.Game.Replays;

/// <summary>
/// Reading a decoded replay's mods: which of them gameplay should run with, how fast the whole
/// thing played, and how to name them in the UI.
/// </summary>
public static class ReplayMods
{
    /// <summary>
    /// The mods gameplay is built with. Everything the replay recorded, minus autoplay — which is
    /// never in a real replay anyway, but would fight the replay for the input handler if it were.
    ///
    /// <para>
    /// Rate-changing mods (DT/NC/HT/DC) ARE included. Unlike the difficulty-affecting ones they sit
    /// beside they change nothing about the rendered playfield: lazer applies them to the TRACK
    /// (<see cref="IApplicableToTrack"/>), which in this app is <see cref="Playback.PlaybackController"/>'s
    /// job (see <see cref="RateFor"/>). Keeping them in the list is what makes the score's own mod
    /// list — and therefore the "HD HR DT" the UI shows — the truth about the play rather than an
    /// edited version of it.
    /// </para>
    ///
    /// <para>
    /// <b>Every call returns FRESH instances</b> (<see cref="Mod.DeepClone"/>, settings included).
    /// Mods are stateful: <c>ModWithVisibilityAdjustment</c> (HD and friends) binds game config
    /// bindables in its <c>ReadFromConfig</c>, which <c>DrawableRuleset</c> calls on load — and
    /// binding an already-bound bindable throws. A dropped replay's decoded score is stored once on
    /// the queue item and outlives many chart rebuilds (difficulty switch, skin change, toggling
    /// the chart off and on), so handing the same instances out twice crashed the app outright.
    /// lazer clones mods per gameplay session for exactly this reason. The stored score's own mods
    /// are therefore never handed to a ruleset — they are the master copy the clones come from.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Mod> ForGameplay(Score? score)
    {
        if (score == null)
            return Array.Empty<Mod>();

        return score.ScoreInfo.Mods.Where(m => m is not ModAutoplay).Select(m => m.DeepClone()).ToArray();
    }

    /// <summary>
    /// How much faster (or slower) the play ran than the beatmap's own timing: 1.5 for DT/NC, 0.75
    /// for HT/DC, 1 with no rate mod. lazer's own <see cref="ModUtils.CalculateRateWithMods"/> does
    /// the combining, so a mod this app has never heard of still contributes correctly.
    /// </summary>
    public static double RateFor(IEnumerable<Mod> mods) => ModUtils.CalculateRateWithMods(mods);

    /// <summary>
    /// The track adjustments these mods want, split by whether they shift pitch:
    /// <c>Tempo</c> changes speed while preserving pitch, <c>Frequency</c> changes both. Multiply
    /// them for the effective playback rate.
    ///
    /// <para>
    /// The split matters because osu!'s rate mods are NOT uniform about it, and getting it wrong is
    /// immediately audible: <b>DoubleTime is pitch-preserving</b> (1.5× tempo) while
    /// <b>Nightcore</b> plays the song higher (1.5× frequency); likewise <b>HalfTime</b> preserves
    /// pitch where <b>Daycore</b> lowers it. Applying everything as frequency made DT sound
    /// chipmunked, which it never does in osu!.
    /// </para>
    ///
    /// <para>
    /// Rather than hard-coding that table, this ASKS THE MODS: each is applied to a throwaway
    /// <see cref="AudioAdjustments"/> exactly as lazer applies them to a real track
    /// (<see cref="IApplicableToTrack"/>), and the aggregate it produces is read back. So whatever
    /// a mod does to audio — including a future one, or a user-configured <c>AdjustPitch</c> — is
    /// what this reports, with no second implementation of the rules to drift out of step.
    /// </para>
    /// </summary>
    public static (double Tempo, double Frequency) TrackAdjustmentsFor(IEnumerable<Mod> mods)
    {
        var probe = new AudioAdjustments();

        foreach (var mod in mods.OfType<IApplicableToTrack>())
            mod.ApplyToTrack(probe);

        return (probe.AggregateTempo.Value, probe.AggregateFrequency.Value);
    }

    /// <summary>
    /// osu!'s own mod ordering — the order of the bits in a .osr's mod field, which is also the
    /// order the game lists them in, so a play everyone knows as "HDHRDT" reads as "HD HR DT"
    /// rather than in whatever order the decoder happened to produce.
    /// </summary>
    private static readonly string[] display_order =
    {
        "NF", "EZ", "TD", "HD", "HR", "SD", "DT", "RX", "HT", "NC", "FL", "AT", "SO", "AP", "PF",
    };

    /// <summary>
    /// Short mod names for the queue card and playback panel, in osu!'s display order. Empty for a
    /// no-mod play, which the UI renders as no row at all.
    ///
    /// <para>
    /// <c>CL</c> (Classic) is left out deliberately. lazer's score decoder attaches it to EVERY
    /// legacy score to mark "this was played under stable's rules" — it is not a mod the player
    /// chose, it is true of every .osr by definition, and osu! itself never shows it. It stays in
    /// the GAMEPLAY list (see <see cref="ForGameplay"/>), where it does real work making slider and
    /// judgement behaviour match what the player actually experienced; it just isn't news.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Acronyms(IEnumerable<Mod> mods)
        => mods.Where(m => m is not ModAutoplay && m is not ModClassic)
               .Select(m => m.Acronym)
               // Unknown acronyms sort after the known ones rather than being dropped.
               .OrderBy(a => Array.IndexOf(display_order, a) is var i && i >= 0 ? i : display_order.Length)
               .ThenBy(a => a, StringComparer.Ordinal)
               .ToArray();

    /// <summary>
    /// Mod names for the multi-replay RAIL, which — unlike <see cref="Acronyms"/> — keeps
    /// <c>CL</c> (Classic) and <c>TD</c> (Touch Device). When you are comparing 47 plays side by
    /// side, "this one was on a tablet" and "this one was under stable's rules" are exactly the sort
    /// of thing worth seeing, where on a single now-playing card they are just noise. TD already
    /// flows through <see cref="Acronyms"/>; CL is the one the general path drops.
    /// </summary>
    public static IReadOnlyList<string> RailAcronyms(IEnumerable<Mod> mods)
        => mods.Where(m => m is not ModAutoplay)
               .Select(m => m.Acronym)
               .OrderBy(a => Array.IndexOf(display_order, a) is var i && i >= 0 ? i : display_order.Length)
               .ThenBy(a => a, StringComparer.Ordinal)
               .ToArray();
}
