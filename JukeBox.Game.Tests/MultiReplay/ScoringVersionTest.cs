#nullable enable

using JukeBox.Game.Replays;
using NUnit.Framework;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;

namespace JukeBox.Game.Tests.MultiReplay
{
    /// <summary>
    /// The scoring-lineage mapping: which tag and formula a replay gets from its two signals — the
    /// legacy-vs-lazer origin and its mods. The load-bearing case is the first one: a stable .osr
    /// carries CL, but it is STILL ScoreV1, because lazer auto-attaches CL to every legacy replay.
    /// </summary>
    [TestFixture]
    public class ScoringVersionTest
    {
        [Test]
        public void AStableReplayIsV1EvenThoughItCarriesClassic()
        {
            // Every stable .osr decodes with CL attached — that must NOT read as lazer-classic.
            var version = ScoringVersions.Detect(isLegacyScore: true, new Mod[] { new OsuModClassic() });

            Assert.That(version, Is.EqualTo(ScoringVersion.V1));
            Assert.That(version.Tag(), Is.EqualTo("V1"));
            Assert.That(version.UsesStableScoreV1(), Is.True);
        }

        [Test]
        public void AStableReplayWithGameplayModsIsStillV1()
        {
            var version = ScoringVersions.Detect(isLegacyScore: true, new Mod[] { new OsuModClassic(), new OsuModHidden(), new OsuModHardRock() });

            Assert.That(version, Is.EqualTo(ScoringVersion.V1));
        }

        [Test]
        public void AGenuineLazerReplayWithClassicIsClassic()
        {
            var version = ScoringVersions.Detect(isLegacyScore: false, new Mod[] { new OsuModClassic() });

            Assert.That(version, Is.EqualTo(ScoringVersion.Classic));
            Assert.That(version.Tag(), Is.EqualTo("Classic"));
            Assert.That(version.UsesStableScoreV1(), Is.False);
        }

        [Test]
        public void AGenuineLazerReplayWithNoDistinguishingModIsLazer()
        {
            var version = ScoringVersions.Detect(isLegacyScore: false, new Mod[] { new OsuModHidden() });

            Assert.That(version, Is.EqualTo(ScoringVersion.Lazer));
            Assert.That(version.Tag(), Is.EqualTo("Lazer"));
            Assert.That(version.UsesStableScoreV1(), Is.False);
        }

        [Test]
        public void AScoreV2ModTakesPrecedenceOverClassic()
        {
            // A lazer play carrying both SV2 and CL is ScoreV2 — SV2 is the stronger signal.
            var version = ScoringVersions.Detect(isLegacyScore: false, new Mod[] { new ModScoreV2(), new OsuModClassic() });

            Assert.That(version, Is.EqualTo(ScoringVersion.V2));
            Assert.That(version.Tag(), Is.EqualTo("V2"));
        }
    }
}
