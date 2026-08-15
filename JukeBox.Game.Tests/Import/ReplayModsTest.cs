#nullable enable

using System.Linq;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Replays;
using NUnit.Framework;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Replays;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;

namespace JukeBox.Game.Tests.Import
{
    /// <summary>
    /// Turning a replay's legacy mod bitfield into the things the rest of the app needs: the mods
    /// gameplay runs with, the playback rate, and the acronyms the UI shows.
    /// </summary>
    [TestFixture]
    public class ReplayModsTest
    {
        // Values straight out of the .osr format's mod bitfield.
        private const int hidden = 8;
        private const int hard_rock = 16;
        private const int double_time = 64;
        private const int half_time = 256;
        private const int nightcore = 512;
        private const int easy = 2;
        private const int flashlight = 1024;

        private static Score scoreWithLegacyMods(int legacyMods, int rulesetId = 0)
        {
            var ruleset = LazerChartLayer.CreateRuleset(rulesetId);

            return new Score
            {
                Replay = new Replay(),
                ScoreInfo = new ScoreInfo
                {
                    Ruleset = ruleset.RulesetInfo,
                    Mods = ruleset.ConvertFromLegacyMods((LegacyMods)legacyMods).ToArray(),
                },
            };
        }

        // The exact bitfield of the real replay this feature was verified against: Cookiezi's
        // HD/HR/DT play on "masterpiece [Insane]" (2013-11-10), mods = 0x58.
        [Test]
        public void TheBitfieldOfARealPlayDecodesToItsAcronymsInOsuOrder()
        {
            var mods = ReplayMods.ForGameplay(scoreWithLegacyMods(hidden | hard_rock | double_time));

            // Ordered, not merely present: this play is universally written "HDHRDT".
            Assert.That(ReplayMods.Acronyms(mods), Is.EqualTo(new[] { "HD", "HR", "DT" }));
        }

        // lazer's decoder attaches Classic to every legacy score to mark "played under stable
        // rules". It is true of every .osr, osu! never displays it, and showing it would put a
        // constant meaningless "CL" on every dropped replay — but it does real work in gameplay,
        // so it must stay in the mod list handed to the ruleset.
        [Test]
        public void TheClassicMarkerIsUsedForGameplayButNotDisplayed()
        {
            var ruleset = LazerChartLayer.CreateRuleset(0);

            var score = new Score
            {
                Replay = new Replay(),
                ScoreInfo = new ScoreInfo
                {
                    Ruleset = ruleset.RulesetInfo,
                    Mods = new Mod[] { new osu.Game.Rulesets.Osu.Mods.OsuModClassic(), new osu.Game.Rulesets.Osu.Mods.OsuModHidden() },
                },
            };

            var mods = ReplayMods.ForGameplay(score);

            Assert.That(mods.Select(m => m.Acronym), Does.Contain("CL"), "gameplay keeps it");
            Assert.That(ReplayMods.Acronyms(mods), Is.EqualTo(new[] { "HD" }), "the UI does not show it");
        }

        [TestCase(double_time, 1.5)]
        [TestCase(nightcore, 1.5)]
        [TestCase(half_time, 0.75)]
        [TestCase(hidden | hard_rock, 1.0)]
        [TestCase(0, 1.0)]
        [TestCase(hidden | hard_rock | double_time, 1.5)]
        public void RateComesFromTheRateChangingMods(int legacyMods, double expectedRate)
        {
            var mods = ReplayMods.ForGameplay(scoreWithLegacyMods(legacyMods));

            Assert.That(ReplayMods.RateFor(mods), Is.EqualTo(expectedRate).Within(0.0001));
        }

        // A .osr has no pitch flag, and every stable rate mod is a straight frequency change — so a
        // rate change always shifts pitch, and no rate change never touches it.
        [TestCase(double_time, true)]
        [TestCase(nightcore, true)]
        [TestCase(half_time, true)]
        [TestCase(hidden | hard_rock, false)]
        [TestCase(0, false)]
        public void PitchShiftsExactlyWhenTheRateDoes(int legacyMods, bool expected)
            => Assert.That(ReplayMods.ShiftsPitch(ReplayMods.ForGameplay(scoreWithLegacyMods(legacyMods))), Is.EqualTo(expected));

        // Rate mods stay in the gameplay list even though they change nothing about the rendered
        // playfield — they are part of what the play WAS, which is what the UI reports.
        [Test]
        public void RateModsAreKeptInTheGameplayModList()
        {
            var mods = ReplayMods.ForGameplay(scoreWithLegacyMods(double_time));

            Assert.That(mods.Any(m => m is IApplicableToRate), Is.True);
            Assert.That(ReplayMods.Acronyms(mods), Does.Contain("DT"));
        }

        // Difficulty- and visual-affecting mods are the ones that actually change what is drawn, so
        // they must survive into the list handed to the ruleset.
        [Test]
        public void DifficultyAndVisualModsSurviveIntoTheGameplayModList()
        {
            var mods = ReplayMods.ForGameplay(scoreWithLegacyMods(easy | flashlight));

            Assert.That(ReplayMods.Acronyms(mods), Is.EqualTo(new[] { "EZ", "FL" }));
            Assert.That(mods.Any(m => m is IApplicableToDifficulty), Is.True, "EZ must still reach difficulty application");
        }

        [Test]
        public void AutoplayIsTheOnlyModDropped()
        {
            var ruleset = LazerChartLayer.CreateRuleset(0);

            var score = new Score
            {
                Replay = new Replay(),
                ScoreInfo = new ScoreInfo
                {
                    Ruleset = ruleset.RulesetInfo,
                    Mods = new Mod[] { ruleset.GetAutoplayMod()!, new osu.Game.Rulesets.Osu.Mods.OsuModHidden() },
                },
            };

            Assert.That(ReplayMods.Acronyms(ReplayMods.ForGameplay(score)), Is.EquivalentTo(new[] { "HD" }));
        }

        [Test]
        public void AnUndecodedReplayHasNoModsAndNoRateChange()
        {
            var none = ReplayMods.ForGameplay(null);

            Assert.That(none, Is.Empty);
            Assert.That(ReplayMods.RateFor(none), Is.EqualTo(1));
            Assert.That(ReplayMods.ShiftsPitch(none), Is.False);
        }

        // Rate mods exist outside osu!std too, and the mapping is per-ruleset.
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void RateModsResolveForEveryRuleset(int rulesetId)
        {
            var mods = ReplayMods.ForGameplay(scoreWithLegacyMods(double_time, rulesetId));

            Assert.That(ReplayMods.RateFor(mods), Is.EqualTo(1.5).Within(0.0001));
        }
    }
}
