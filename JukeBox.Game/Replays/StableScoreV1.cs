#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Scoring;

namespace JukeBox.Game.Replays;

/// <summary>
/// osu!STABLE ScoreV1 for an osu! play, the score osu!stable actually gave and the one danser
/// reproduces — NOT lazer's <c>GetDisplayScore(ScoringMode.Classic)</c>, which is a monotonic remap of
/// the standardised score and does not match stable's number (it read ~52.6M where stable/danser read
/// ~37M for the same play).
///
/// <para>
/// The formula (osu! wiki, ScoreV1): each 300/100/50 scores <c>value · (1 + max(comboBefore-1,0) ·
/// difficultyMultiplier · modMultiplier / 25)</c>; a slider as a whole produces one 300/100/50 scored
/// that way, its ticks add a flat 10 and its repeats/tail a flat 30 (no combo bonus), and the slider
/// head grants no points of its own. The difficulty multiplier is stable's "old star rating",
/// <c>round((HP + CS + OD + clamp(objectCount / drainSeconds · 8, 0, 16)) / 38 · 5)</c> on the map's
/// ORIGINAL stats (mods never change it), and the mod multiplier is the product of the active mods'
/// stable multipliers.
/// </para>
///
/// <para>
/// Fed the same ordered per-object results both the drawable renderer and the analytic judge already
/// produce (object + <see cref="HitResult"/>), so it is exact given the judgements — and it corrects
/// the score on BOTH paths, the shipping drawable one included.
/// </para>
/// </summary>
public sealed class StableScoreV1
{
    private readonly double scoreMultiplier;

    private long score;
    private int combo;

    public StableScoreV1(IBeatmap original, IReadOnlyList<Mod> mods)
    {
        scoreMultiplier = difficultyMultiplier(original) * modMultiplier(mods);
    }

    /// <summary>The running stable score after every result applied so far.</summary>
    public long Score => score;

    /// <summary>Applies one judged object in play order, updating the running score and the internal
    /// combo the next combo bonus reads.</summary>
    public void Apply(HitObject obj, HitResult result)
    {
        int value = baseValue(obj, result);

        if (carriesComboBonus(result))
        {
            // The combo bonus, on the combo BEFORE this hit (wiki: max(combo - 1, 0)).
            long bonusCombo = Math.Max(combo - 1, 0);
            score += value + (long)(value * bonusCombo * scoreMultiplier / 25.0);
        }
        else
        {
            score += value;
        }

        // Combo: every hit part of a slider or a circle raises it; a miss or a large-tick miss (a
        // dropped slider tick) resets it. The SLIDER AGGREGATE never raises combo itself — its head,
        // ticks and tail already did — so it is not double-counted. Small-tick (tail) misses are
        // lenient and do not break combo.
        if (obj is Slider)
        {
            // The aggregate: score only, no combo change (and its miss does not itself break — the
            // head/tick misses already handled that).
        }
        else if (result is HitResult.Great or HitResult.Ok or HitResult.Meh or HitResult.LargeTickHit or HitResult.SmallTickHit)
        {
            combo++;
        }
        else if (result is HitResult.Miss or HitResult.LargeTickMiss)
        {
            combo = 0;
        }
    }

    /// <summary>Stable base value for a result: 300/100/50 for accuracy hits and the slider whole,
    /// 10 for a slider tick, 30 for a repeat or the tail, nothing for the head or any miss.</summary>
    private static int baseValue(HitObject obj, HitResult result) => result switch
    {
        HitResult.Great => 300,
        HitResult.Ok => 100,
        HitResult.Meh => 50,
        HitResult.LargeTickHit => obj is SliderTick ? 10 : obj is SliderHeadCircle ? 0 : 30,
        HitResult.SmallTickHit => 30,
        _ => 0,
    };

    /// <summary>Whether a result takes the combo bonus (the 300/100/50 accuracy hits — circles and the
    /// slider whole); ticks, repeats and the tail add their flat value only.</summary>
    private static bool carriesComboBonus(HitResult result)
        => result is HitResult.Great or HitResult.Ok or HitResult.Meh;

    /// <summary>stable's difficulty multiplier ("old star rating"), on the ORIGINAL map stats.</summary>
    private static int difficultyMultiplier(IBeatmap beatmap)
    {
        var difficulty = beatmap.Difficulty;
        int objectCount = beatmap.HitObjects.Count;

        double first = beatmap.HitObjects.Count == 0 ? 0 : beatmap.HitObjects[0].StartTime;
        double last = beatmap.HitObjects.Count == 0 ? 0 : beatmap.HitObjects[^1].GetEndTime();
        double breakTime = beatmap.Breaks.Sum(b => b.Duration);
        double drainSeconds = Math.Max((last - first - breakTime) / 1000.0, 1);

        double stars = (difficulty.DrainRate + difficulty.CircleSize + difficulty.OverallDifficulty
                        + Math.Clamp(objectCount / drainSeconds * 8.0, 0, 16)) / 38.0 * 5.0;

        return (int)Math.Round(stars, MidpointRounding.AwayFromZero);
    }

    /// <summary>The product of the active mods' stable score multipliers.</summary>
    private static double modMultiplier(IReadOnlyList<Mod> mods)
    {
        double multiplier = 1;

        foreach (var mod in mods)
        {
            multiplier *= mod.Acronym switch
            {
                "EZ" => 0.50,
                "NF" => 0.50,
                "HT" or "DC" => 0.30,
                "HR" => 1.06,
                "HD" => 1.06,
                "DT" or "NC" => 1.12,
                "FL" => 1.12,
                "SO" => 0.90,
                _ => 1.0,
            };
        }

        return multiplier;
    }
}
