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
    // the fullscreen listing's difficulty strip and expansion rows: each mode must yield LAZER'S
    // REAL ruleset icon (a SpriteIcon over the texture-backed OsuIcon glyphs — see the
    // OsuIconStore registration in JukeBoxGameBase), not a FontAwesome approximation.
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

            var icon = FullscreenBeatmapCard.CreateRulesetIcon(mode);

            Assert.That(icon, Is.InstanceOf<SpriteIcon>(), "lazer's four rulesets all create SpriteIcon-based icons");
            Assert.That(((SpriteIcon)icon).Icon, Is.EqualTo(expected));
            Assert.That(((SpriteIcon)icon).Icon.FontName, Is.EqualTo(OsuIcon.FONT_NAME),
                "the glyph must come from lazer's texture-backed icon store, not FontAwesome");
        }

        [Test]
        public void ModeStringsMapToTheExpectedRulesets()
        {
            Assert.That(FullscreenBeatmapCard.RulesetFor("osu"), Is.InstanceOf<OsuRuleset>());
            Assert.That(FullscreenBeatmapCard.RulesetFor("taiko"), Is.InstanceOf<TaikoRuleset>());
            Assert.That(FullscreenBeatmapCard.RulesetFor("fruits"), Is.InstanceOf<CatchRuleset>());
            Assert.That(FullscreenBeatmapCard.RulesetFor("catch"), Is.InstanceOf<CatchRuleset>());
            Assert.That(FullscreenBeatmapCard.RulesetFor("mania"), Is.InstanceOf<ManiaRuleset>());
        }
    }
}
