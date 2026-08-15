#nullable enable

using JukeBox.Game.Configuration;
using JukeBox.Game.Detach;
using JukeBox.Game.LazerPlayer;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Testing;
using osu.Game.Configuration;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Configuration;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The SENDING half of the sync protocol: what the main process puts in a snapshot.
    /// <see cref="TestSceneViewerScreen"/> covers what the viewer does with one, and
    /// <see cref="TestSceneSettingsMirror"/> the registry underneath both — but neither would
    /// notice a main process that built a snapshot without the settings in it, which is a
    /// perfectly silent way for the viewer to stop honouring everything at once.
    /// </summary>
    [TestFixture]
    public partial class TestSceneViewerSyncSender : JukeBoxTestScene
    {
        [Resolved]
        private JukeBoxConfigManager config { get; set; } = null!;

        [Resolved]
        private OsuConfigManager lazerConfig { get; set; } = null!;

        [Resolved]
        private IRulesetConfigCache rulesetConfigs { get; set; } = null!;

        [Resolved]
        private SkinSelection skinSelection { get; set; } = null!;

        private DetachedViewerManager manager = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create manager", () => Child = manager = new DetachedViewerManager());
            AddUntilStep("manager loaded", () => manager.IsLoaded);
        }

        [TearDownSteps]
        public void TearDownSteps()
        {
            AddStep("restore defaults", () =>
            {
                config.SetValue(JukeBoxSetting.ChartMods, string.Empty);
                config.SetValue(JukeBoxSetting.ConvertToRuleset, ChartConversionTarget.Off);
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Argon);
                lazerConfig.SetValue(OsuSetting.HitLighting, false);

                if (rulesetConfigs.GetConfigFor(new ManiaRuleset()) is ManiaRulesetConfigManager mania)
                    mania.SetValue(ManiaRulesetSetting.ScrollSpeed, 8.0);
            });
        }

        [Test]
        public void SnapshotCarriesEverySettingsManager()
        {
            AddStep("change settings across all three", () =>
            {
                config.SetValue(JukeBoxSetting.ChartMods, "HD,HR");
                config.SetValue(JukeBoxSetting.ConvertToRuleset, ChartConversionTarget.Mania);
                lazerConfig.SetValue(OsuSetting.HitLighting, true);

                if (rulesetConfigs.GetConfigFor(new ManiaRuleset()) is ManiaRulesetConfigManager mania)
                    mania.SetValue(ManiaRulesetSetting.ScrollSpeed, 19.0);
            });

            AddAssert("ours is in the snapshot", () =>
            {
                var settings = manager.BuildState().Settings;
                return settings["jukebox:ChartMods"] == "HD,HR" && settings["jukebox:ConvertToRuleset"] == "Mania";
            });

            AddAssert("lazer's is too", () => manager.BuildState().Settings["lazer:HitLighting"] == "1");
            AddAssert("and the ruleset's", () => manager.BuildState().Settings["ruleset:ManiaRulesetSetting.ScrollSpeed"] == "19");
        }

        /// <summary>
        /// Random must be rolled ONCE, by the main process: two windows rolling independently would
        /// show two different skins for the same song, which is exactly what the viewer is not.
        /// </summary>
        [Test]
        public void SnapshotSendsTheResolvedSkinNeverRandom()
        {
            AddStep("pick Random", () => config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Random));

            AddAssert("a concrete skin is sent", () =>
            {
                string sent = manager.BuildState().Skin;
                return sent != nameof(JukeBoxSkin.Random) && sent == skinSelection.Effective.Value.ToString();
            });

            AddAssert("and it is not in the settings dictionary", () => !manager.BuildState().Settings.ContainsKey("jukebox:Skin"));
        }
    }
}
