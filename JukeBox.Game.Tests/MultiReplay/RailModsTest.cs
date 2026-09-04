#nullable enable

using System.Linq;
using JukeBox.Game.Replays;
using NUnit.Framework;
using osu.Game.Rulesets.Osu.Mods;

namespace JukeBox.Game.Tests.MultiReplay
{
    /// <summary>
    /// The rail drops <c>CL</c> just as the general display does — its old job of marking "played
    /// under stable's rules" now belongs to the scoring-version TAG (see
    /// <see cref="ScoringVersions.Tag"/>), so keeping "+CL" too would be redundant. It still keeps
    /// <c>TD</c> ("on a tablet") and the real mods.
    /// </summary>
    [TestFixture]
    public class RailModsTest
    {
        [Test]
        public void TheRailDropsClassicButKeepsTouchDeviceAndTheRealMods()
        {
            var mods = new osu.Game.Rulesets.Mods.Mod[]
            {
                new OsuModClassic(),
                new OsuModTouchDevice(),
                new OsuModHardRock(),
            };

            var general = ReplayMods.Acronyms(mods);
            var rail = ReplayMods.RailAcronyms(mods);

            // The general display drops CL (every legacy score has it) but keeps TD.
            Assert.That(general, Does.Not.Contain("CL"));
            Assert.That(general, Does.Contain("TD"));

            // The rail now drops CL too — the version tag carries that — while keeping the rest.
            Assert.That(rail, Does.Not.Contain("CL"));
            Assert.That(rail, Does.Contain("TD"));
            Assert.That(rail, Does.Contain("HR"));
        }

        [Test]
        public void TheRailStillDropsAutoplay()
        {
            var rail = ReplayMods.RailAcronyms(new osu.Game.Rulesets.Mods.Mod[] { new OsuModAutoplay(), new OsuModHidden() });

            Assert.That(rail, Is.EqualTo(new[] { "HD" }));
        }
    }
}
