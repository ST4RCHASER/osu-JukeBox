#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Configuration;
using JukeBox.Game.Detach;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
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
                config.SetValue(JukeBoxSetting.ChartOpacity, 1.0);
                config.SetValue(JukeBoxSetting.RemoveChartMask, false);
                config.SetValue(JukeBoxSetting.RemoveStoryboardMask, false);
                lazerConfig.SetValue(OsuSetting.HitLighting, false);
                osuRulesetConfig()?.SetValue(OsuRulesetSetting.SnakingInSliders, true);
                maniaRulesetConfig()?.SetValue(ManiaRulesetSetting.ScrollSpeed, 8.0);
                watchedScrollSpeed = null;
                watchedDim = null;
                watchedHitLighting = null;
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

            // Every setting that changes what the player RENDERS has to be here or the detached
            // window quietly disagrees with the main one — these three all do.
            AddAssert("covers the chart opacity and both mask releases",
                () => mirror.Keys.Contains("jukebox:ChartOpacity")
                      && mirror.Keys.Contains("jukebox:RemoveChartMask")
                      && mirror.Keys.Contains("jukebox:RemoveStoryboardMask"));

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
                ["jukebox:ChartOpacity"] = "0.4",
                ["jukebox:RemoveStoryboardMask"] = "1",
            }));

            AddAssert("ours took it", () => config.Get<double>(JukeBoxSetting.BackgroundDim) == 0.85);
            AddAssert("so did the new render settings", () => config.Get<double>(JukeBoxSetting.ChartOpacity) == 0.4
                                                              && config.Get<bool>(JukeBoxSetting.RemoveStoryboardMask));
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

            // Asserted against the SETTINGS, not just against a second capture: an encoding that
            // truncated (a double written as an integer, say) would round-trip perfectly against
            // itself while quietly rounding the user's value on every hop.
            AddAssert("and the values themselves are intact", () =>
                config.Get<double>(JukeBoxSetting.BackgroundDim) == 0.123456789
                && config.Get<JukeBox.Game.LazerPlayer.ChartConversionTarget>(JukeBoxSetting.ConvertToRuleset) == JukeBox.Game.LazerPlayer.ChartConversionTarget.Mania
                && maniaRulesetConfig()?.Get<ManiaScrollingDirection>(ManiaRulesetSetting.ScrollDirection) == ManiaScrollingDirection.Up
                && osuRulesetConfig()?.Get<int>(OsuRulesetSetting.ReplayAnalysisDisplayLength) == 1234);
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

        // Bound copies held in FIELDS so a collection mid-test can't drop the subscriptions these
        // two tests count on — the same weak-reference hazard the mirror itself guards against.
        private Bindable<double>? watchedScrollSpeed;
        private Bindable<double>? watchedDim;
        private Bindable<bool>? watchedHitLighting;

        private int scrollSpeedChanges, dimChanges, hitLightingChanges;

        /// <summary>
        /// Subscribes to one setting from each of the three config managers and counts how many
        /// times each is actually CHANGED, which is what the manager persists (for a ruleset
        /// setting, into realm).
        /// </summary>
        private void watchWrites()
        {
            watchedScrollSpeed = maniaRulesetConfig()!.GetBindable<double>(ManiaRulesetSetting.ScrollSpeed);
            watchedDim = config.GetBindable<double>(JukeBoxSetting.BackgroundDim);
            watchedHitLighting = lazerConfig.GetBindable<bool>(OsuSetting.HitLighting);

            watchedScrollSpeed.ValueChanged += _ => scrollSpeedChanges++;
            watchedDim.ValueChanged += _ => dimChanges++;
            watchedHitLighting.ValueChanged += _ => hitLightingChanges++;

            scrollSpeedChanges = dimChanges = hitLightingChanges = 0;
        }

        /// <summary>
        /// The viewer re-applies the WHOLE snapshot at 4 Hz, so this is the property that keeps
        /// that free: assigning a bindable the value it already holds changes nothing, and a
        /// setting that isn't changed is never persisted. It is asserted rather than assumed
        /// because it rests on osu.Framework's equality short-circuit in <c>Bindable.Value</c> —
        /// if that ever went away, the viewer would start writing every registered setting to
        /// realm four times a second and nothing else would notice.
        /// </summary>
        [Test]
        public void ReapplyingAnUnchangedSnapshotWritesNothing()
        {
            Dictionary<string, string> snapshot = null!;

            AddUntilStep("registry populated", () => mirror.Keys.Any());
            AddStep("capture and settle", () =>
            {
                snapshot = mirror.Capture();
                mirror.Apply(snapshot);
            });

            AddStep("start counting", watchWrites);
            AddStep("re-apply the same snapshot ten times", () =>
            {
                for (int i = 0; i < 10; i++)
                    mirror.Apply(snapshot);
            });

            AddAssert("no setting was written", () => scrollSpeedChanges == 0 && dimChanges == 0 && hitLightingChanges == 0);
        }

        /// <summary>
        /// And the same property under the worst case for it — a slider being dragged in the main
        /// window, which sends a different value every heartbeat. The dragged setting is written
        /// once per tick, which is inherent; every OTHER setting in the registry must stay silent.
        /// </summary>
        [Test]
        public void DraggingOneSettingWritesOnlyThatSetting()
        {
            const int ticks = 40; // ten seconds of the 4 Hz heartbeat

            Dictionary<string, string> snapshot = null!;

            AddUntilStep("registry populated", () => mirror.Keys.Any());
            AddStep("capture and settle", () =>
            {
                snapshot = mirror.Capture();
                mirror.Apply(snapshot);
            });

            AddStep("start counting", watchWrites);
            AddStep($"drag the scroll speed across {ticks} snapshots", () =>
            {
                for (int i = 0; i < ticks; i++)
                {
                    snapshot["ruleset:ManiaRulesetSetting.ScrollSpeed"] = (5 + i * 0.25).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    mirror.Apply(snapshot);
                }
            });

            AddAssert("the dragged one moved once per tick", () => scrollSpeedChanges == ticks);
            AddAssert("nothing else moved at all", () => dimChanges == 0 && hitLightingChanges == 0);
        }
    }
}
