#nullable enable

using JukeBox.Game.UI;
using NUnit.Framework;

namespace JukeBox.Game.Tests.UI
{
    // The string rule behind DifficultySwitcher's rating lookup. osu-web decorates a mania
    // difficulty's name with its key count when serving it ("[4K] NOVICE"), while the .osu file on
    // disk carries the plain name — so the two sides only line up once the decoration is off. Cases
    // are real ones taken from beatmapsets 653740 and 1974347.
    [TestFixture]
    public class ManiaKeyPrefixTest
    {
        [TestCase("[4K] NOVICE", "NOVICE")]
        [TestCase("[4K] Chicken's HEAVENLY", "Chicken's HEAVENLY")]
        [TestCase("[7K] Insane", "Insane")]
        [TestCase("[10K] Hard", "Hard")]
        [TestCase("[18K] Extra", "Extra")]
        public void TheKeyCountPrefixIsStripped(string served, string expected)
        {
            Assert.That(DifficultySwitcher.StripManiaKeyPrefix(served), Is.EqualTo(expected));
        }

        // Everything else must survive untouched — most importantly a key count the MAPPER wrote
        // into the name, which osu! serves back unchanged and which therefore already matches.
        [TestCase("14K DP Hard")]
        [TestCase("10K Easy")]
        [TestCase("4K Lunatic")]
        [TestCase("Insane")]
        [TestCase("Chicken's HEAVENLY")]
        [TestCase("[Extra]")]
        [TestCase("[Bracketed] Name")]
        [TestCase("Not [4K] leading")]
        public void EveryOtherNameIsLeftAlone(string version)
        {
            Assert.That(DifficultySwitcher.StripManiaKeyPrefix(version), Is.EqualTo(version));
        }
    }
}
