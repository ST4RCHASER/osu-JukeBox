#nullable enable

using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Taiko;

namespace JukeBox.Game.Tests.UI
{
    // Covers the BeatmapInfo.Mode string -> lazer ruleset -> Ruleset.CreateIcon() mapping used by
    // the fullscreen listing's difficulty strip and expansion rows AND by the now-playing
    // difficulty dropdown: each mode must yield LAZER'S REAL ruleset icon (a SpriteIcon over the
    // texture-backed OsuIcon glyphs — see the OsuIconStore registration in JukeBoxGameBase), not a
    // FontAwesome approximation.
    [TestFixture]
    public class RulesetIconTest
    {
        [TestCase("osu")]
        [TestCase("taiko")]
        [TestCase("fruits")]
        [TestCase("catch")]
        [TestCase("mania")]
        [TestCase("some-future-mode")]
        public void EveryModeYieldsTheMatchingLazerRulesetIcon(string mode)
        {
            var expected = mode switch
            {
                "taiko" => OsuIcon.RulesetTaiko,
                "fruits" or "catch" => OsuIcon.RulesetCatch,
                "mania" => OsuIcon.RulesetMania,
                _ => OsuIcon.RulesetOsu, // "osu" and unknown modes fall back to osu!'s icon
            };

            var icon = RulesetIcons.Create(mode);

            Assert.That(icon, Is.InstanceOf<SpriteIcon>(), "lazer's four rulesets all create SpriteIcon-based icons");
            Assert.That(((SpriteIcon)icon).Icon, Is.EqualTo(expected));
            Assert.That(((SpriteIcon)icon).Icon.FontName, Is.EqualTo(OsuIcon.FONT_NAME),
                "the glyph must come from lazer's texture-backed icon store, not FontAwesome");
        }

        [Test]
        public void ModeStringsMapToTheExpectedRulesets()
        {
            Assert.That(RulesetIcons.For("osu"), Is.InstanceOf<OsuRuleset>());
            Assert.That(RulesetIcons.For("taiko"), Is.InstanceOf<TaikoRuleset>());
            Assert.That(RulesetIcons.For("fruits"), Is.InstanceOf<CatchRuleset>());
            Assert.That(RulesetIcons.For("catch"), Is.InstanceOf<CatchRuleset>());
            Assert.That(RulesetIcons.For("mania"), Is.InstanceOf<ManiaRuleset>());
        }

        // The local difficulty side of the same mapping: a scanned .osu file carries [General] Mode
        // as an integer, and the dropdown has to turn that into the same mode string the online
        // metadata uses — both to pick the icon and to match a difficulty against its star rating.
        [TestCase(0, "osu")]
        [TestCase(1, "taiko")]
        [TestCase(2, "fruits")]
        [TestCase(3, "mania")]
        public void LocalModeIntegersMapToOnlineModeStrings(int mode, string expected)
        {
            Assert.That(RulesetIcons.ModeString(mode), Is.EqualTo(expected));
            Assert.That(RulesetIcons.For(RulesetIcons.ModeString(mode)), Is.EqualTo(RulesetIcons.For(expected)));
        }

        [Test]
        public void UnknownLocalModeStillYieldsAnIcon()
        {
            // An unrecognised future mode plays chartless rather than breaking — it must not match
            // any online mode string (so no star rating is misattributed to it) but must still draw.
            Assert.That(RulesetIcons.ModeString(99), Is.Not.EqualTo("osu"));
            Assert.That(RulesetIcons.Create(99), Is.InstanceOf<SpriteIcon>());
        }
    }
}
