#nullable enable

using System;
using System.IO;
using System.Linq;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Game.Beatmaps;
using osu.Game.Replays;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Scoring;
using JukeBox.Game.Tests.Visual;
using osuTK;

namespace JukeBox.Game.Tests.MultiReplay
{
    /// <summary>
    /// Items #15/#16: the GLOBAL default (Chart tab mods, Settings tab skin) and the PER-PLAYER
    /// override (Playback tab) working TOGETHER, asserted on the mods and skin the rendered chart
    /// actually built with (<see cref="LazerChartLayer.ActiveMods"/> / <see cref="LazerChartLayer.SelectedSkin"/>),
    /// not on the store fields feeding them.
    ///
    /// <para>
    /// The confirmed contract, which these pin end to end:
    /// <list type="bullet">
    /// <item><b>Global default</b> — the baseline every player renders under. In a MULTI-REPLAY view
    /// (<see cref="LazerChartLayer.UseRecordedReplayModsOnly"/>) that per-player baseline is the
    /// player's OWN recorded mods, deliberately NOT the shared Chart-tab selection: a replay already
    /// happened under mods of its own, and pointing every player at the one shared selection was the
    /// bug that put player one's mods on all of them (fixed in 53f7d2c). The skin has no such split —
    /// its global default is the Settings-tab selection for everyone.</item>
    /// <item><b>Override wins</b> — a per-player mod or skin override replaces that baseline for that
    /// one player's render and score.</item>
    /// <item><b>Clearing falls back</b> — with no override the player renders under the global default
    /// again (recorded mods; the global skin).</item>
    /// </list>
    /// </para>
    /// </summary>
    [TestFixture]
    public partial class OverrideResolutionTest : JukeBoxTestScene
    {
        private JukeBoxConfigManager config = null!;
        private SkinSelection skinSelection = null!;
        private Container layerHost = null!;

        private readonly ManualClock manual = new ManualClock();

        private string dir = null!;
        private LazerChartLayer layer = null!;

        // Own config (ini in temp storage) and skin service bound to it, so choosing a global skin
        // here never touches the developer's real settings.
        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-override-resolution-test", Path.GetRandomFileName())));
            skinSelection = new SkinSelection();

            var deps = new DependencyContainer(parent);
            deps.Cache(config);
            deps.Cache(skinSelection);
            return deps;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Add(skinSelection);
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
            AddStep("reset global skin to Argon", () =>
            {
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Argon);
                layerHost.Clear();
            });
        }

        // ---- mods: global default (recorded) + override wins + clear falls back ----------------

        /// <summary>
        /// With no override, a multi-replay player renders under their OWN recorded mods — the
        /// per-player global default. A mutant that dropped the recorded set (rendering unmodded, or
        /// reaching for the shared selection) fails here.
        /// </summary>
        [Test]
        public void APlayerWithNoModOverrideRendersUnderItsRecordedMods()
        {
            buildReplayLayer(recorded: new Mod[] { new OsuModHidden() }, overrideMods: null);

            AddAssert("the chart runs under the recorded HD, nothing else", () =>
                layer.ActiveMods.Select(m => m.Acronym).ToArray().SequenceEqual(new[] { "HD" }));
        }

        /// <summary>
        /// A per-player mod override replaces the recorded mods for that player's render — the
        /// Playback tab winning over the recorded baseline. HR must be what the chart runs under, and
        /// the recorded HD must be gone (proving the override REPLACED rather than added to it).
        /// </summary>
        [Test]
        public void APerPlayerModOverrideWinsOverTheRecordedMods()
        {
            buildReplayLayer(recorded: new Mod[] { new OsuModHidden() }, overrideMods: new Mod[] { new OsuModHardRock() });

            AddAssert("the chart runs under the HR override", () => layer.ActiveMods.Any(m => m.Acronym == "HR"));
            AddAssert("and the recorded HD was displaced by it", () => layer.ActiveMods.All(m => m.Acronym != "HD"));
        }

        /// <summary>
        /// Clearing the override (no per-player mods) falls the player back to their recorded mods,
        /// NOT to whatever the last override was. Built with no override against a recorded HD set:
        /// the chart must read HD and carry no trace of the HR another player might wear.
        /// </summary>
        [Test]
        public void ClearingTheModOverrideFallsBackToTheRecordedMods()
        {
            buildReplayLayer(recorded: new Mod[] { new OsuModHidden() }, overrideMods: null);

            AddAssert("back on the recorded HD", () => layer.ActiveMods.Any(m => m.Acronym == "HD"));
            AddAssert("with no override mod left in force", () => layer.ActiveMods.All(m => m.Acronym != "HR"));
        }

        // ---- skin: global default (Settings tab) + override wins + clear falls back ------------

        /// <summary>
        /// With no per-player skin override, the player renders under the GLOBAL Settings-tab skin.
        /// The global is set to Triangles here, so a mutant ignoring the global (falling to the bare
        /// Argon default) fails.
        /// </summary>
        [Test]
        public void APlayerWithNoSkinOverrideRendersUnderTheGlobalSkin()
        {
            selectGlobalSkin(JukeBoxSkin.Triangles);
            buildReplayLayer(recorded: Array.Empty<Mod>(), overrideMods: null);

            AddUntilStep("layer built", () => layer.IsLoaded && layer.DrawableRuleset != null);
            AddAssert("built under the global Triangles skin", () => layer.SelectedSkin == JukeBoxSkin.Triangles);
        }

        /// <summary>
        /// A per-player skin override wins over the global skin: the global is Triangles, but this
        /// player is forced to Classic and must build under Classic.
        /// </summary>
        [Test]
        public void APerPlayerSkinOverrideWinsOverTheGlobalSkin()
        {
            selectGlobalSkin(JukeBoxSkin.Triangles);
            buildReplayLayer(recorded: Array.Empty<Mod>(), overrideMods: null, overrideSkinKey: nameof(JukeBoxSkin.Classic));

            AddUntilStep("layer built", () => layer.IsLoaded && layer.DrawableRuleset != null);
            AddAssert("built under the Classic override, not the global Triangles", () => layer.SelectedSkin == JukeBoxSkin.Classic);
        }

        /// <summary>
        /// Clearing the skin override falls the player back to the global skin. Built with no
        /// override against a global of Triangles: it must read Triangles, not the bare Argon default
        /// a mutant that skipped the global would land on.
        /// </summary>
        [Test]
        public void ClearingTheSkinOverrideFallsBackToTheGlobalSkin()
        {
            selectGlobalSkin(JukeBoxSkin.Triangles);
            buildReplayLayer(recorded: Array.Empty<Mod>(), overrideMods: null, overrideSkinKey: null);

            AddUntilStep("layer built", () => layer.IsLoaded && layer.DrawableRuleset != null);
            AddAssert("back on the global Triangles skin", () => layer.SelectedSkin == JukeBoxSkin.Triangles);
        }

        private void selectGlobalSkin(JukeBoxSkin skin)
        {
            AddStep($"set global skin to {skin}", () => config.SetValue(JukeBoxSetting.Skin, skin));
            AddUntilStep("global skin resolved", () => skinSelection.Effective.Value == skin);
        }

        /// <summary>
        /// Builds a single multi-replay chart layer (<see cref="LazerChartLayer.UseRecordedReplayModsOnly"/>
        /// on, as the grid and combine views build it) from a replay recorded under
        /// <paramref name="recorded"/>, optionally with a per-player mod or skin override — exactly
        /// the inputs <see cref="Replays.PlayerOverrideStore"/> supplies at render time.
        /// </summary>
        private void buildReplayLayer(Mod[] recorded, Mod[]? overrideMods, string? overrideSkinKey = null)
        {
            AddStep("build the layer", () =>
            {
                manual.CurrentTime = 0;

                string osu = Path.Combine(dir, $"override [{Guid.NewGuid():N}].osu");
                File.WriteAllText(osu,
                    "osu file format v14\n\n[General]\nAudioFilename: audio.wav\nMode: 0\n\n"
                    + "[Metadata]\nTitle:test\nArtist:test\nCreator:test\nVersion:test\n\n"
                    + "[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:5\nApproachRate:5\nSliderMultiplier:1.4\nSliderTickRate:1\n\n"
                    + "[TimingPoints]\n0,500,4,1,0,100,1,0\n\n"
                    + "[HitObjects]\n64,192,1000,1,0\n192,192,1500,1,0\n");

                var ruleset = LazerChartLayer.CreateRuleset(0);

                // The recorded play: its mods are the ScoreInfo the render path reads back through
                // ReplayMods.ForGameplay. A single frame is enough to drive the ruleset.
                var score = new Score
                {
                    Replay = new Replay { Frames = { new OsuReplayFrame(0, new Vector2(64, 96)) } },
                    ScoreInfo = new ScoreInfo
                    {
                        Ruleset = ruleset.RulesetInfo,
                        Mods = recorded,
                    },
                };

                layerHost.Child = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = new FramedClock(manual),
                    Child = layer = new LazerChartLayer(new FlatWorkingBeatmap(osu), osu, score)
                    {
                        UseRecordedReplayModsOnly = true,
                        OverrideMods = overrideMods,
                        OverrideSkinKey = overrideSkinKey,
                    },
                };
            });

            AddUntilStep("layer loaded", () => layer.IsLoaded && layer.DrawableRuleset != null);
        }
    }
}
