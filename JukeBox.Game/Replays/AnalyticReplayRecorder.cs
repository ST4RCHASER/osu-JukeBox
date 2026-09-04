#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.LazerPlayer;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu.Scoring;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;

namespace JukeBox.Game.Replays;

/// <summary>
/// Turns an osu! replay into a <see cref="ReplayTimeline"/> WITHOUT a gameplay renderer: the
/// judgements come from <see cref="AnalyticOsuJudge"/> (pure geometry, one linear pass) and every
/// NUMBER — score, combo, accuracy, rank, pp — comes from feeding those judgements into lazer's own
/// <see cref="OsuScoreProcessor"/>, exactly as the drawable simulator's score processor would have
/// produced them. This is the danser-speed path: a whole map in milliseconds, no 16ms-stepped
/// renderer, and nothing to stall on a slider-heavy section.
///
/// <para>
/// The per-judgement recording — classic-score conversion for CL plays, combo-break detection and
/// its lost-combo size, live pp — is the same logic the drawable path used, moved here and driven
/// off the analytic results instead of a renderer's NewResult event.
/// </para>
/// </summary>
public static class AnalyticReplayRecorder
{
    /// <summary>What a recording produced, kept past the (non-existent) renderer's life.</summary>
    public readonly record struct Recorded(IReadOnlyList<Mod> Mods, ScoringMode ScoringMode);

    /// <summary>
    /// Records <paramref name="replayScore"/> on <paramref name="working"/> under <paramref name="mods"/>
    /// INTO <paramref name="timeline"/>, marking it complete. Recording into a caller-owned timeline
    /// (rather than returning a fresh one) keeps the instance the scoreboard already captured stable
    /// as it fills in. <paramref name="attributeCache"/> is shared across plays of the same mod set so
    /// the map's difficulty attributes are computed once, not per player.
    /// </summary>
    public static Recorded Record(IWorkingBeatmap working, Ruleset ruleset, IReadOnlyList<Mod> mods, Score replayScore, Dictionary<string, DifficultyAttributes> attributeCache, ReplayTimeline timeline)
    {
        var playable = working.GetPlayableBeatmap(ruleset.RulesetInfo, mods);
        var frames = replayScore.Replay?.Frames ?? new List<osu.Game.Rulesets.Replays.ReplayFrame>();

        var judged = AnalyticOsuJudge.Evaluate(playable, frames);

        var processor = new OsuScoreProcessor();
        processor.ApplyBeatmap(playable);

        var performance = ReplayPerformance.Create(ruleset, working, mods, attributeCache);

        // A CL play (every legacy .osr, or a per-player Classic override) is SCORED the classic way —
        // its rail number and its knockout ranking are the classic score, not lazer's standardised
        // one. Same conversion the drawable path used: lazer's own PopulateScore → GetDisplayScore,
        // into a single reused ScoreInfo.
        bool classic = mods.Any(m => m is ModClassic);
        var scoringMode = classic ? ScoringMode.Classic : ScoringMode.Standardised;
        var classicScore = classic ? new ScoreInfo { Ruleset = ruleset.RulesetInfo } : null;

        int lastCombo = 0;

        foreach (var j in judged)
        {
            var jr = new JudgementResult(j.Object, j.Object.CreateJudgement()) { Type = j.Result };
            processor.ApplyResult(jr);

            int combo = processor.Combo.Value;

            // A break is a held combo going to zero — judged on the combo transition, not the result
            // type, so a miss on the very first object (no combo yet) is not counted as a break.
            bool broke = lastCombo > 0 && combo == 0;
            int lost = broke ? lastCombo : 0;
            lastCombo = combo;

            long total;

            if (classicScore != null)
            {
                processor.PopulateScore(classicScore);
                total = classicScore.GetDisplayScore(ScoringMode.Classic);
            }
            else
            {
                total = processor.TotalScore.Value;
            }

            timeline.Record(new TimelinePoint(
                j.Time,
                total,
                combo,
                processor.Accuracy.Value,
                broke,
                // The RAW rank name — skins name grade graphics after it ("ranking-X-small"); the row
                // converts for display.
                processor.Rank.Value.ToString(),
                performance?.PointsFor(processor) ?? 0,
                lost,
                j.Result,
                j.Position));
        }

        double endTime = playable.HitObjects.Count == 0 ? 0 : playable.HitObjects[^1].GetEndTime();
        timeline.MarkComplete(endTime);

        return new Recorded(mods, scoringMode);
    }
}
