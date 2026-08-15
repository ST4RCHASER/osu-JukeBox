#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Configuration;
using JukeBox.Game.Detach;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Testing;
using osu.Game.Configuration;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Configuration;
using osu.Game.Rulesets.Mania.UI;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Configuration;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The one mechanism the detached viewer window honours settings through: everything the main
    /// process <see cref="SettingsMirror.Capture"/>s, a viewer process <see cref="SettingsMirror.Apply"/>s
    /// back into its own config managers. Tested at the mechanism level (and across all three
    /// managers), because the per-setting cost of being wrong here is the same for every setting in
    /// the registry — <see cref="TestSceneViewerScreen"/> then proves the wire actually carries it.
    /// </summary>
    [TestFixture]
    public partial class TestSceneSettingsMirror : JukeBoxTestScene
    {
        [Resolved]
        private SettingsMirror mirror { get; set; } = null!;

        [Resolved]
        private JukeBoxConfigManager config { get; set; } = null!;

        [Resolved]
        private OsuConfigManager lazerConfig { get; set; } = null!;

        [Resolved]
        private IRulesetConfigCache rulesetConfigs { get; set; } = null!;

        [TearDownSteps]
        public void TearDownSteps()
        {
            // This scene writes into the shared test-browser config managers — put back what the
            // rest of the suite expects.
            AddStep("restore defaults", () =>
            {
                config.SetValue(JukeBoxSetting.BackgroundDim, 0.3);
                config.SetValue(JukeBoxSetting.ChartMods, string.Empty);
                lazerConfig.SetValue(OsuSetting.HitLighting, false);
                osuRulesetConfig()?.SetValue(OsuRulesetSetting.SnakingInSliders, true);
                maniaRulesetConfig()?.SetValue(ManiaRulesetSetting.ScrollSpeed, 8.0);
            });
        }

        private OsuRulesetConfigManager? osuRulesetConfig() => rulesetConfigs.GetConfigFor(new OsuRuleset()) as OsuRulesetConfigManager;
        private ManiaRulesetConfigManager? maniaRulesetConfig() => rulesetConfigs.GetConfigFor(new ManiaRuleset()) as ManiaRulesetConfigManager;

        /// <summary>
        /// The registry has to reach all three config managers, or a whole class of settings is
        /// silently unsynced with nothing failing.
        /// </summary>
        [Test]
        public void RegistryCoversAllThreeConfigManagers()
        {
            AddUntilStep("registry populated", () => mirror.Keys.Any());

            AddAssert("covers our own config", () => mirror.Keys.Contains("jukebox:ChartMods")
                                                     && mirror.Keys.Contains("jukebox:HiddenPlayfieldElements")
                                                     && mirror.Keys.Contains("jukebox:ConvertToRuleset"));

            AddAssert("covers lazer's game-wide config", () => mirror.Keys.Contains("lazer:HitLighting"));

            AddAssert("covers the per-ruleset configs", () => mirror.Keys.Contains("ruleset:OsuRulesetSetting.SnakingInSliders")
                                                             && mirror.Keys.Contains("ruleset:TaikoRulesetSetting.HitAnimations")
                                                             && mirror.Keys.Contains("ruleset:ManiaRulesetSetting.ScrollDirection"));

            AddAssert("keys are unique", () => mirror.Keys.Distinct().Count() == mirror.Keys.Count());
        }

        /// <summary>
        /// Capture reads the LIVE value, not one sampled when the registry was built. The GC is
        /// forced deliberately: <c>ConfigManager.GetBindable</c> hands back a bound copy the manager
        /// holds only WEAKLY, so a registry that didn't keep its own reference would pass this test
        /// right up until a collection ran and then report the stale value forever.
        /// </summary>
        [Test]
        public void CaptureFollowsLaterChangesAcrossACollection()
        {
            AddStep("change values", () =>
            {
                config.SetValue(JukeBoxSetting.ChartMods, "HD,DT");
                lazerConfig.SetValue(OsuSetting.HitLighting, true);
                maniaRulesetConfig()?.SetValue(ManiaRulesetSetting.ScrollSpeed, 17.0);
            });

            AddStep("collect garbage", () =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            });

            AddAssert("ours captured", () => mirror.Capture()["jukebox:ChartMods"] == "HD,DT");
            AddAssert("lazer's captured", () => mirror.Capture()["lazer:HitLighting"] == "1");
            AddAssert("ruleset's captured", () => mirror.Capture()["ruleset:ManiaRulesetSetting.ScrollSpeed"] == "17");

            AddStep("change them again", () =>
            {
                config.SetValue(JukeBoxSetting.ChartMods, "EZ");
                lazerConfig.SetValue(OsuSetting.HitLighting, false);
                maniaRulesetConfig()?.SetValue(ManiaRulesetSetting.ScrollSpeed, 3.0);
            });

            AddAssert("capture moved with them", () =>
            {
                var captured = mirror.Capture();
                return captured["jukebox:ChartMods"] == "EZ"
                       && captured["lazer:HitLighting"] == "0"
                       && captured["ruleset:ManiaRulesetSetting.ScrollSpeed"] == "3";
            });
        }

        [Test]
        public void ApplyWritesIntoEveryManager()
        {
            AddUntilStep("registry populated", () => mirror.Keys.Any());

            AddStep("apply a captured set", () => mirror.Apply(new Dictionary<string, string>
            {
                ["jukebox:BackgroundDim"] = "0.85",
                ["lazer:HitLighting"] = "1",
                ["ruleset:OsuRulesetSetting.SnakingInSliders"] = "0",
                ["ruleset:ManiaRulesetSetting.ScrollSpeed"] = "21",
            }));

            AddAssert("ours took it", () => config.Get<double>(JukeBoxSetting.BackgroundDim) == 0.85);
            AddAssert("lazer's took it", () => lazerConfig.Get<bool>(OsuSetting.HitLighting));
            AddAssert("osu! ruleset took it", () => osuRulesetConfig()?.Get<bool>(OsuRulesetSetting.SnakingInSliders) == false);
            AddAssert("mania ruleset took it", () => maniaRulesetConfig()?.Get<double>(ManiaRulesetSetting.ScrollSpeed) == 21);
        }

        /// <summary>
        /// Capture and Apply are the same registry read in two directions, so a full round trip has
        /// to be the identity — this is what stops a setting being capturable but not appliable (or
        /// encoded one way and decoded another, which for a double would drift by a hair per hop).
        /// </summary>
        [Test]
        public void RoundTripIsTheIdentityForEveryRegisteredSetting()
        {
            Dictionary<string, string> before = null!;

            AddStep("set some awkward values", () =>
            {
                config.SetValue(JukeBoxSetting.BackgroundDim, 0.123456789);
                config.SetValue(JukeBoxSetting.ConvertToRuleset, JukeBox.Game.LazerPlayer.ChartConversionTarget.Mania);
                maniaRulesetConfig()?.SetValue(ManiaRulesetSetting.ScrollDirection, ManiaScrollingDirection.Up);
                osuRulesetConfig()?.SetValue(OsuRulesetSetting.ReplayAnalysisDisplayLength, 1234);
            });

            AddStep("capture", () => before = mirror.Capture());
            AddStep("apply it straight back", () => mirror.Apply(before));

            AddAssert("nothing moved", () =>
            {
                var after = mirror.Capture();
                return after.Count == before.Count && before.All(pair => after[pair.Key] == pair.Value);
            });
        }

        /// <summary>
        /// A viewer built from different source, or a torn value, must cost exactly one setting —
        /// not the rest of the snapshot.
        /// </summary>
        [Test]
        public void UnknownKeysAndUnparseableValuesAreSkipped()
        {
            AddUntilStep("registry populated", () => mirror.Keys.Any());
            AddStep("known starting dim", () => config.SetValue(JukeBoxSetting.BackgroundDim, 0.3));

            AddStep("apply junk alongside a good value", () => mirror.Apply(new Dictionary<string, string>
            {
                ["jukebox:SomethingFromTheFuture"] = "whatever",
                ["jukebox:BackgroundDim"] = "not a number",
                ["lazer:HitLighting"] = "1",
            }));

            AddAssert("the good one landed", () => lazerConfig.Get<bool>(OsuSetting.HitLighting));
            AddAssert("the torn one was left alone", () => config.Get<double>(JukeBoxSetting.BackgroundDim) == 0.3);
        }
    }
}
