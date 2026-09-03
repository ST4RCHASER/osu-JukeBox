#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Logging;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// Performance points for a play in progress, using lazer's own calculators so the number matches
/// what osu! would give.
///
/// <para>
/// The expensive half is the beatmap's difficulty attributes, which take a full pass over the map.
/// They depend only on the beatmap and the MOD SET, though — not on how anybody played — so they
/// are computed once per distinct mod set and shared. With 47 replays of one map there are
/// typically a handful of distinct mod sets rather than 47, which is the difference between one
/// pass and dozens.
/// </para>
/// </summary>
public sealed class ReplayPerformance
{
    private readonly PerformanceCalculator calculator;
    private readonly DifficultyAttributes attributes;
    private readonly ScoreInfo template;

    private ReplayPerformance(PerformanceCalculator calculator, DifficultyAttributes attributes, ScoreInfo template)
    {
        this.calculator = calculator;
        this.attributes = attributes;
        this.template = template;
    }

    /// <summary>
    /// Points for the state <paramref name="score"/> is in right now, or zero if the ruleset gives
    /// no answer.
    /// </summary>
    public double PointsFor(ScoreProcessor score)
    {
        template.Accuracy = score.Accuracy.Value;
        template.MaxCombo = score.HighestCombo.Value;
        template.TotalScore = score.TotalScore.Value;
        template.Statistics = new Dictionary<HitResult, int>(score.Statistics);

        try
        {
            return calculator.Calculate(template, attributes).Total;
        }
        catch (Exception e)
        {
            // A ruleset that cannot score this combination must not take the whole board down; a
            // missing pp column is survivable, a crashed scoreboard is not.
            Logger.Error(e, "[performance] could not compute pp for this play");
            return 0;
        }
    }

    /// <summary>
    /// Builds a calculator for one play, reusing difficulty attributes already computed for the
    /// same mod set. Null when the ruleset provides no performance calculator.
    /// </summary>
    public static ReplayPerformance? Create(Ruleset ruleset, IWorkingBeatmap beatmap, IReadOnlyList<Mod> mods, Dictionary<string, DifficultyAttributes> cache)
    {
        try
        {
            var calculator = ruleset.CreatePerformanceCalculator();

            if (calculator == null)
                return null;

            // Keyed by the mod set, since that is all the attributes depend on. Sorted so that the
            // same mods in a different order are recognised as the same work.
            string key = string.Join("|", mods.Select(m => m.Acronym).OrderBy(a => a, StringComparer.Ordinal));

            if (!cache.TryGetValue(key, out var attributes))
            {
                attributes = ruleset.CreateDifficultyCalculator(beatmap).Calculate(mods);
                cache[key] = attributes;
            }

            var template = new ScoreInfo
            {
                Ruleset = ruleset.RulesetInfo,
                Mods = mods.ToArray(),
            };

            return new ReplayPerformance(calculator, attributes, template);
        }
        catch (Exception e)
        {
            Logger.Error(e, "[performance] could not prepare pp calculation for this play");
            return null;
        }
    }
}
