#nullable enable

using System;
using JukeBox.Game.Replays;
using NUnit.Framework;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Scoring;

namespace JukeBox.Game.Tests.MultiReplay
{
    /// <summary>
    /// osu!stable ScoreV1 arithmetic, pinned to a hand-computable case. This is the number the play
    /// scored on stable and that danser reproduces — validated in situ (on beatmap 2809623 the
    /// worst_hr_player replay reads ~36.6M at combo 1481, matching danser's ~37M, where lazer's
    /// GetDisplayScore(Classic) read a wildly-off ~52.6M).
    /// </summary>
    [TestFixture]
    public class StableScoreV1Test
    {
        // Three circles at 1s/2s/3s, HP=CS=OD=5. drain = 2s, count = 3, so the clamp term is
        // clamp(3/2*8,0,16)=12, and the difficulty multiplier is round((5+5+5+12)/38*5)=round(3.55)=4.
        private static Beatmap threeCircleMap()
        {
            var beatmap = new Beatmap
            {
                BeatmapInfo = new BeatmapInfo
                {
                    Difficulty = new BeatmapDifficulty { DrainRate = 5, CircleSize = 5, OverallDifficulty = 5 },
                },
            };

            beatmap.HitObjects.Add(new HitCircle { StartTime = 1000 });
            beatmap.HitObjects.Add(new HitCircle { StartTime = 2000 });
            beatmap.HitObjects.Add(new HitCircle { StartTime = 3000 });
            return beatmap;
        }

        [Test]
        public void ThreeGreatsNoModsScoreTheComboBonusFormula()
        {
            var map = threeCircleMap();
            var score = new StableScoreV1(map, Array.Empty<Mod>());

            // scoreMultiplier = difficultyMultiplier(4) * modMultiplier(1) = 4.
            // Hit 1: combo 0 -> bonus max(0-1,0)=0 -> 300.
            // Hit 2: combo 1 -> bonus max(1-1,0)=0 -> 300.
            // Hit 3: combo 2 -> bonus max(2-1,0)=1 -> 300 + (long)(300*1*4/25)=300+48 = 348.
            foreach (var circle in map.HitObjects)
                score.Apply(circle, HitResult.Great);

            Assert.That(score.Score, Is.EqualTo(300 + 300 + 348));
        }

        [Test]
        public void HardRockMultipliesTheComboBonusByItsStableMultiplier()
        {
            var map = threeCircleMap();
            var score = new StableScoreV1(map, new Mod[] { new OsuModHardRock() });

            // scoreMultiplier = 4 * 1.06 = 4.24 (the difficulty multiplier stays on ORIGINAL stats —
            // Hard Rock does not raise it). Hit 3 bonus = (long)(300*1*4.24/25) = (long)50.88 = 50.
            foreach (var circle in map.HitObjects)
                score.Apply(circle, HitResult.Great);

            Assert.That(score.Score, Is.EqualTo(300 + 300 + 350));
        }

        [Test]
        public void SliderPartsScoreFlatValuesAndTheWholeCarriesTheComboBonus()
        {
            var map = threeCircleMap();
            var score = new StableScoreV1(map, Array.Empty<Mod>());
            var slider = new Slider();

            // A slider's parts add flat stable values with no combo bonus, and increment combo; the
            // slider head grants none.
            score.Apply(new SliderHeadCircle(), HitResult.LargeTickHit);       // head: 0, combo -> 1
            score.Apply(new SliderTick(), HitResult.LargeTickHit);             // tick: 10, combo -> 2
            score.Apply(new SliderTailCircle(slider), HitResult.SmallTickHit); // tail: 30, combo -> 3

            long afterParts = score.Score;
            Assert.That(afterParts, Is.EqualTo(0 + 10 + 30), "parts add flat values, no combo bonus");

            // The slider WHOLE (its aggregate) carries the 300 with the combo bonus at the combo its
            // parts built: combo 3 -> max(3-1,0)=2 -> 300 + (long)(300*2*4/25)=300+96=396.
            score.Apply(slider, HitResult.Great);

            Assert.That(score.Score - afterParts, Is.EqualTo(300 + 96));
        }
    }
}
