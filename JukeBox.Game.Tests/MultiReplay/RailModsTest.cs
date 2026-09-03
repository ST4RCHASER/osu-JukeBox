#nullable enable

using System.Linq;
using JukeBox.Game.Replays;
using NUnit.Framework;
using osu.Game.Rulesets.Osu.Mods;

namespace JukeBox.Game.Tests.MultiReplay
{
    /// <summary>
    /// The rail names more mods than the now-playing card does. Comparing 47 plays, "this one was on
    /// a tablet" (TD) and "this one was under stable's rules" (CL) are worth seeing; on a single card
    /// they are noise. This pins that the rail keeps both while the general display still drops CL.
    /// </summary>
    [TestFixture]
    public class RailModsTest
    {
        [Test]
        public void TheRailKeepsClassicAndTouchDeviceWhereTheGeneralDisplayDropsClassic()
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

            // The rail keeps BOTH, alongside the real mods.
            Assert.That(rail, Does.Contain("CL"));
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
