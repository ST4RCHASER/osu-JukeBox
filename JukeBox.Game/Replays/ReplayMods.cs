#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Rate-changing mods (DT/NC/HT/DC) ARE included, unlike the difficulty-affecting ones they sit
    /// beside they change nothing about the rendered playfield: lazer applies them to the TRACK
    /// (<see cref="IApplicableToTrack"/>), which in this app is <see cref="Playback.PlaybackController"/>'s
    /// job (see <see cref="RateFor"/>). Keeping them in the list is what makes the score's own mod
    /// list — and therefore the "HD HR DT" the UI shows — the truth about the play rather than an
    /// edited version of it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Mod> ForGameplay(Score? score)
    {
        if (score == null)
            return Array.Empty<Mod>();

        return score.ScoreInfo.Mods.Where(m => m is not ModAutoplay).ToArray();
    }

    /// <summary>
    /// How much faster (or slower) the play ran than the beatmap's own timing: 1.5 for DT/NC, 0.75
    /// for HT/DC, 1 with no rate mod. lazer's own <see cref="ModUtils.CalculateRateWithMods"/> does
    /// the combining, so a mod this app has never heard of still contributes correctly.
    /// </summary>
    public static double RateFor(IEnumerable<Mod> mods) => ModUtils.CalculateRateWithMods(mods);

    /// <summary>
    /// Whether playing at <see cref="RateFor"/> should shift the audio's PITCH as well as its speed.
    ///
    /// <para>
    /// Always true when there is a rate change, because a .osr is by definition a stable replay and
    /// every stable rate mod is a straight frequency change — DT and NC play the song higher, HT and
    /// DC lower. The mod objects lazer hands back after decoding carry an <c>AdjustPitch</c> setting
    /// sitting at lazer's own default (false), but that is lazer's UI preference for a mod the USER
    /// selects; the .osr format has no pitch flag at all, so it says nothing about how this play
    /// actually sounded. Reproducing what the player heard means pitching it.
    /// </para>
    /// </summary>
    public static bool ShiftsPitch(IEnumerable<Mod> mods) => Math.Abs(RateFor(mods) - 1) > 0.0001;

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
}
