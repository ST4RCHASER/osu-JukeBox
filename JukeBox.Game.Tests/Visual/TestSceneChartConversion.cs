#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Playback;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Taiko;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Playing a beatmap as another mode: which maps lazer says can be converted, that each target
    /// really produces that ruleset's own objects, and that a convert is the state in which
    /// osu!mania's key count and Co-op finally do something.
    /// </summary>
    [TestFixture]
    public partial class TestSceneChartConversion : JukeBoxTestScene
    {
        private JukeBoxConfigManager config = null!;
        private PlaybackController playback = null!;
        private ChartModSelection selection = null!;
        private ChartConversion conversion = null!;
        private Container layerHost = null!;

        private readonly ManualClock manual = new ManualClock();

        private string dir = null!;
        private LazerChartLayer layer = null!;

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-conversion-test", Path.GetRandomFileName())));
            playback = new PlaybackController();
            selection = new ChartModSelection();
            conversion = new ChartConversion();

            var deps = new DependencyContainer(parent);
            deps.Cache(config);
            deps.CacheAs(playback);
            deps.Cache(selection);
            deps.Cache(conversion);
            return deps;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Add(playback);
            Add(selection);
            Add(conversion);
            Add(layerHost = new Container { RelativeSizeAxes = Axes.Both });
        }

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("reset", () =>
            {
                config.SetValue(JukeBoxSetting.ChartMods, string.Empty);
                config.SetValue(JukeBoxSetting.ConvertToRuleset, ChartConversionTarget.Off);
                layerHost.Clear();
            });

            AddUntilStep("services ready", () => conversion.IsLoaded && selection.IsLoaded);
        }

        /// <summary>
        /// Convertibility is lazer's answer, not ours: an osu! map converts to the other three, and
        /// a map already native to taiko/catch/mania converts to none of them. Asked of the target
        /// ruleset's own <c>IBeatmapConverter.CanConvert</c>.
        /// </summary>
        [TestCase(0, true)]
        [TestCase(1, false)]
        [TestCase(2, false)]
        [TestCase(3, false)]
        public void OnlyOsuMapsAreConvertible(int mode, bool convertible)
        {
            AddAssert($"a mode-{mode} map is convertible: {convertible}",
                () => ChartConversion.ConvertibleToAnything(beatmap(mode)) == convertible);

            // And per target, which is what the dropdown's entries mean one at a time.
            foreach (Ruleset target in new Ruleset[] { new TaikoRuleset(), new CatchRuleset(), new ManiaRuleset() })
            {
                var captured = target;

                // Its OWN ruleset is trivially fine and is asserted separately below.
                if (captured.RulesetInfo.OnlineID == mode)
                    continue;

                AddAssert($"mode-{mode} to {captured.ShortName}: {convertible}",
                    () => ChartConversion.CanConvert(beatmap(mode), captured) == convertible);
            }

            AddAssert("every map is trivially 'convertible' to its own ruleset",
                () => ChartConversion.CanConvert(beatmap(mode), LazerChartLayer.CreateRuleset(mode)));
        }

        /// <summary>
        /// Each target really renders the beatmap as that ruleset — its own DrawableRuleset, and a
        /// playable beatmap made of that ruleset's own hit-object types rather than osu!'s.
        /// </summary>
        [TestCase(ChartConversionTarget.Taiko, 1, typeof(TaikoRuleset), typeof(osu.Game.Rulesets.Taiko.Objects.TaikoHitObject))]
        [TestCase(ChartConversionTarget.Catch, 2, typeof(CatchRuleset), typeof(osu.Game.Rulesets.Catch.Objects.CatchHitObject))]
        [TestCase(ChartConversionTarget.Mania, 3, typeof(ManiaRuleset), typeof(osu.Game.Rulesets.Mania.Objects.ManiaHitObject))]
        public void AnOsuMapRendersAsTheChosenMode(ChartConversionTarget target, int rulesetId, Type ruleset, Type hitObject)
        {
            AddStep($"convert to {target}", () => config.SetValue(JukeBoxSetting.ConvertToRuleset, target));
            buildLayer(0);

            AddUntilStep("layer loaded", () => layer.IsLoaded && layer.PlayableBeatmap != null);

            AddAssert("built with the target ruleset", () => layer.Ruleset?.GetType() == ruleset);
            AddAssert("and the beatmap converted to its objects",
                () => layer.PlayableBeatmap!.HitObjects.Count > 0
                      && layer.PlayableBeatmap!.HitObjects.All(hitObject.IsInstanceOfType));

            AddAssert("the service agrees it is converting",
                () => conversion.IsConverting.Value && conversion.EffectiveRulesetId.Value == rulesetId);

            AddStep("advance", () => manual.CurrentTime = 1200);
            AddUntilStep("the playfield populates", () => layer.DrawableRuleset!.Playfield.AllHitObjects.Any());

            AddStep("clean up", () => layerHost.Clear());
        }

        /// <summary>A native map is left alone whatever the target says — there is nothing to convert
        /// it to, so it renders in its own mode rather than failing or rendering empty.</summary>
        [Test]
        public void ANativeMapIsRenderedInItsOwnModeWhateverIsSelected()
        {
            AddStep("ask for taiko", () => config.SetValue(JukeBoxSetting.ConvertToRuleset, ChartConversionTarget.Taiko));
            buildLayer(3);

            AddUntilStep("layer loaded", () => layer.IsLoaded && layer.PlayableBeatmap != null);

            AddAssert("still mania", () => layer.Ruleset?.GetType() == typeof(ManiaRuleset));
            AddAssert("and the service reports no conversion", () => !conversion.IsConverting.Value);

            AddStep("clean up", () => layerHost.Clear());
        }

        /// <summary>
        /// The whole point of the feature for osu!mania: the key count and Co-op reach the beatmap
        /// through the CONVERTER, so under conversion they finally change the stage — measured as
        /// real column and stage counts, not as a mods list.
        /// </summary>
        [Test]
        public void UnderConversionTheKeyCountAndCoopChangeTheManiaStage()
        {
            AddStep("convert to mania", () => config.SetValue(JukeBoxSetting.ConvertToRuleset, ChartConversionTarget.Mania));
            buildLayer(0);

            AddUntilStep("layer loaded", () => layer.IsLoaded && layer.PlayableBeatmap != null);
            AddStep("record the default column count", () => defaultColumns = maniaBeatmap().TotalColumns);
            AddAssert("one stage by default", () => maniaBeatmap().Stages.Count == 1);

            AddStep("select 7 keys", () => selection.Enabled(ChartMod.Key7).Value = true);
            AddStep("rebuild", () =>
            {
                layerHost.Clear();
                buildLayerNow(0);
            });

            AddUntilStep("7K layer loaded", () => layer.IsLoaded && layer.PlayableBeatmap != null);
            AddAssert("the convert really has 7 columns now", () => maniaBeatmap().TotalColumns == 7);

            AddStep("add Co-op", () => selection.Enabled(ChartMod.DualStages).Value = true);
            AddStep("rebuild", () =>
            {
                layerHost.Clear();
                buildLayerNow(0);
            });

            AddUntilStep("co-op layer loaded", () => layer.IsLoaded && layer.PlayableBeatmap != null);
            AddAssert("two stages", () => maniaBeatmap().Stages.Count == 2);
            AddAssert("and twice the columns", () => maniaBeatmap().TotalColumns == 14);

            // The contrast that makes the point: the very same mods on a NATIVE mania map do nothing.
            AddStep("stop converting", () => config.SetValue(JukeBoxSetting.ConvertToRuleset, ChartConversionTarget.Off));
            AddStep("build a native 4K map with the same mods", () =>
            {
                layerHost.Clear();
                buildLayerNow(3);
            });

            AddUntilStep("native layer loaded", () => layer.IsLoaded && layer.PlayableBeatmap != null);
            AddAssert("its own stage is untouched",
                () => maniaBeatmap().TotalColumns == 4 && maniaBeatmap().Stages.Count == 1);

            AddStep("clean up", () => layerHost.Clear());
        }

        private int defaultColumns;

        /// <summary>Mods are resolved for the ruleset actually being rendered, so a converted chart
        /// gets the TARGET's mods and never an osu!-only one the target has no answer for.</summary>
        [Test]
        public void ModsReResolveToTheTargetRuleset()
        {
            AddStep("select HD + 7 keys", () =>
            {
                selection.Enabled(ChartMod.Hidden).Value = true;
                selection.Enabled(ChartMod.Key7).Value = true;
            });

            AddStep("convert to mania", () => config.SetValue(JukeBoxSetting.ConvertToRuleset, ChartConversionTarget.Mania));
            buildLayer(0);

            AddUntilStep("layer loaded", () => layer.IsLoaded && layer.DrawableRuleset != null);

            AddAssert("the chart runs mania's own HD and 7K", () =>
            {
                var mods = layer.DrawableRuleset!.Mods.Where(m => m.Acronym is "HD" or "7K").ToArray();
                var mania = new ManiaRuleset().CreateAllMods().ToArray();

                return mods.Length == 2 && mods.All(m => mania.Any(a => a.GetType() == m.GetType()));
            });

            AddAssert("and the selection judges rules against mania now",
                () => selection.CurrentRulesetId == 3);

            AddStep("clean up", () => layerHost.Clear());
        }

        [Test]
        public void TheTargetRoundTripsThroughConfig()
        {
            AddStep("choose catch", () => conversion.Target.Value = ChartConversionTarget.Catch);
            AddAssert("persisted", () => config.Get<ChartConversionTarget>(JukeBoxSetting.ConvertToRuleset) == ChartConversionTarget.Catch);

            AddStep("a config written by something else lands",
                () => config.SetValue(JukeBoxSetting.ConvertToRuleset, ChartConversionTarget.Taiko));

            AddAssert("the service followed it", () => conversion.Target.Value == ChartConversionTarget.Taiko);
        }

        /// <summary>
        /// A replay's frames belong to the ruleset it was played on, so a conversion must not touch
        /// it — rendering a replay as another mode would be showing input that never happened.
        /// </summary>
        [Test]
        public void AReplayIsNeverConverted()
        {
            AddStep("convert to mania", () => config.SetValue(JukeBoxSetting.ConvertToRuleset, ChartConversionTarget.Mania));

            AddStep("build an osu! chart from a replay", () =>
            {
                manual.CurrentTime = 0;

                string osu = Path.Combine(dir, "replayed [0].osu");
                File.WriteAllText(osu, beatmapText(0));

                string osr = Path.Combine(dir, "replayed.osr");
                Import.ReplayFixture.Write(osr, osu, "Cookiezi");
                var score = new Replays.JukeBoxScoreDecoder(osu).Decode(osr);

                layerHost.Child = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = new FramedClock(manual),
                    Child = layer = new LazerChartLayer(new FlatWorkingBeatmap(osu), osu, score),
                };
            });

            AddUntilStep("layer loaded", () => layer.IsLoaded && layer.DrawableRuleset != null);

            AddAssert("it stayed osu!, replay-driven", () => layer.Ruleset?.GetType() == typeof(OsuRuleset) && layer.UsingUserReplay);

            AddStep("clean up", () => layerHost.Clear());
        }

        private osu.Game.Rulesets.Mania.Beatmaps.ManiaBeatmap maniaBeatmap()
            => (osu.Game.Rulesets.Mania.Beatmaps.ManiaBeatmap)layer.PlayableBeatmap!;

        private WorkingBeatmap beatmap(int mode)
        {
            string osu = Path.Combine(dir, $"probe [{mode}].osu");

            if (!File.Exists(osu))
                File.WriteAllText(osu, beatmapText(mode));

            return new FlatWorkingBeatmap(osu);
        }

        private void buildLayer(int mode) => AddStep($"build a mode-{mode} chart", () => buildLayerNow(mode));

        private void buildLayerNow(int mode)
        {
            manual.CurrentTime = 0;

            string osu = Path.Combine(dir, $"chart [{mode}].osu");
            File.WriteAllText(osu, beatmapText(mode));

            layerHost.Child = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Clock = new FramedClock(manual),
                Child = layer = new LazerChartLayer(new FlatWorkingBeatmap(osu), osu),
            };
        }

        private static string beatmapText(int mode) =>
            "osu file format v14\n\n[General]\nAudioFilename: audio.wav\nMode: " + mode + "\n\n"
            + "[Metadata]\nTitle:test\nArtist:test\nCreator:test\nVersion:test\n\n"
            + "[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:5\nApproachRate:5\nSliderMultiplier:1.4\nSliderTickRate:1\n\n"
            + "[TimingPoints]\n0,500,4,1,0,100,1,0\n\n"
            + "[HitObjects]\n64,192,1000,1,0\n192,192,1500,1,0\n320,192,2000,1,0\n"
            + (mode == 3 ? "448,192,2500,128,0,3500:0:0:0:0:\n" : "128,192,2500,2,0,L|320:192,1,192\n");
    }
}
