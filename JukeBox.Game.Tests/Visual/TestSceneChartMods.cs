#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using JukeBox.Game.Replays;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Taiko;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The Chart tab's mod selection: what lazer says may be worn together, what reaches a ruleset,
    /// and — the part that is audible the moment it is wrong — what each rate mod does to the track.
    /// </summary>
    [TestFixture]
    public partial class TestSceneChartMods : JukeBoxTestScene
    {
        private JukeBoxConfigManager config = null!;
        private PlaybackController playback = null!;
        private ChartModSelection selection = null!;

        /// <summary>Hosts the chart layer built by the last test below; a container of its own so
        /// clearing it never takes the services added beside it down too.</summary>
        private Container layerHost = null!;

        private readonly ManualClock manual = new ManualClock();

        /// <summary>The game-level jukebox (never started here) — the selection reads its
        /// <see cref="Jukebox.NowPlaying"/> to know whether a replay is in control, so the tests
        /// publish one there directly.</summary>
        [Resolved]
        private Jukebox jukebox { get; set; } = null!;

        // Own config and playback controller, the config being ini-in-temp-storage so these tests
        // neither read nor write the developer's real settings (same isolation
        // TestSceneSettingsOverlay uses).
        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-chart-mods-test", Path.GetRandomFileName())));
            playback = new PlaybackController();
            selection = new ChartModSelection();

            var deps = new DependencyContainer(parent);
            deps.Cache(config);
            deps.CacheAs(playback);
            deps.Cache(selection);
            return deps;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Add(playback);
            Add(selection);
            Add(layerHost = new Container { RelativeSizeAxes = Axes.Both });
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            // Clearing the persisted value is what resets the selection — it applies straight back
            // onto the bindables, which is the same path a fresh launch takes.
            AddStep("clear the selection", () =>
            {
                config.SetValue(JukeBoxSetting.ChartMods, string.Empty);
                jukebox.NowPlaying.Value = null;
                layerHost.Clear();
            });

            AddUntilStep("selection loaded and empty", () => selection.IsLoaded && selection.Selected.Count == 0);
        }

        /// <summary>
        /// DT, NC and HT are all <c>ModRateAdjust</c>, so lazer refuses any two of them together.
        /// The rule is READ from the mods (see <see cref="ChartModSelection.Compatible"/>) rather
        /// than written down here, so this test is the proof that reading it actually produces the
        /// exclusions everyone expects.
        /// </summary>
        [Test]
        public void OnlyOneRateModCanBeOnAtATime()
        {
            AddStep("enable DT", () => selection.Enabled(ChartMod.DoubleTime).Value = true);
            AddAssert("DT on", () => selection.Enabled(ChartMod.DoubleTime).Value);

            AddStep("enable NC", () => selection.Enabled(ChartMod.Nightcore).Value = true);
            AddAssert("NC on", () => selection.Enabled(ChartMod.Nightcore).Value);
            AddAssert("DT switched itself off", () => !selection.Enabled(ChartMod.DoubleTime).Value);

            AddStep("enable HT", () => selection.Enabled(ChartMod.HalfTime).Value = true);
            AddAssert("HT on", () => selection.Enabled(ChartMod.HalfTime).Value);
            AddAssert("NC switched itself off", () => !selection.Enabled(ChartMod.Nightcore).Value);
            AddAssert("exactly one rate mod is on", () => selection.Selected.Count == 1);
        }

        [Test]
        public void EasyAndHardRockAreMutuallyExclusiveButCoexistWithEverythingElse()
        {
            AddStep("enable EZ + HD + FL", () =>
            {
                selection.Enabled(ChartMod.Easy).Value = true;
                selection.Enabled(ChartMod.Hidden).Value = true;
                selection.Enabled(ChartMod.Flashlight).Value = true;
            });

            AddAssert("all three stayed on", () => selection.Selected.Count == 3);

            AddStep("enable HR", () => selection.Enabled(ChartMod.HardRock).Value = true);

            AddAssert("EZ switched itself off", () => !selection.Enabled(ChartMod.Easy).Value);
            AddAssert("HD and FL were left alone",
                () => selection.Enabled(ChartMod.Hidden).Value && selection.Enabled(ChartMod.Flashlight).Value);
        }

        /// <summary>
        /// The one that is immediately audible when wrong: DoubleTime and HalfTime change SPEED
        /// while preserving pitch (tempo), Nightcore changes both (frequency). Driving all of them
        /// through frequency is what makes DT sound chipmunked, which it never does in osu!.
        /// </summary>
        [TestCase(ChartMod.DoubleTime, 1.5, 1.0)]
        [TestCase(ChartMod.Nightcore, 1.0, 1.5)]
        [TestCase(ChartMod.HalfTime, 0.75, 1.0)]
        public void RateModsSplitCorrectlyBetweenTempoAndFrequency(ChartMod mod, double tempo, double frequency)
        {
            AddAssert("nothing forcing a rate to begin with",
                () => playback.ChartModTempo.Value == 1 && playback.ChartModFrequency.Value == 1);

            AddStep($"enable {mod.Acronym()}", () => selection.Enabled(mod).Value = true);

            AddAssert($"{mod.Acronym()} moves tempo to {tempo}", () => Math.Abs(playback.ChartModTempo.Value - tempo) < 1e-9);
            AddAssert($"{mod.Acronym()} moves frequency to {frequency}", () => Math.Abs(playback.ChartModFrequency.Value - frequency) < 1e-9);

            AddStep($"disable {mod.Acronym()}", () => selection.Enabled(mod).Value = false);

            AddAssert("the forced rate was released",
                () => playback.ChartModTempo.Value == 1 && playback.ChartModFrequency.Value == 1);
        }

        [Test]
        public void NonRateModsLeaveThePlaybackRateAlone()
        {
            AddStep("enable HD + HR + FL + EZ-free set", () =>
            {
                selection.Enabled(ChartMod.Hidden).Value = true;
                selection.Enabled(ChartMod.HardRock).Value = true;
                selection.Enabled(ChartMod.Flashlight).Value = true;
            });

            AddAssert("playback still runs at 1×",
                () => playback.ChartModTempo.Value == 1 && playback.ChartModFrequency.Value == 1);
        }

        /// <summary>
        /// A replay is a record of a play that already happened under mods of its own, so the user's
        /// selection must not additionally speed the track up while one is watched — the replay's
        /// own rate (ReplayTempo/ReplayFrequency, set by <see cref="Jukebox"/>) is the only one in
        /// force.
        /// </summary>
        [Test]
        public void AReplayInControlSuspendsTheSelectionsRate()
        {
            AddStep("enable DT", () => selection.Enabled(ChartMod.DoubleTime).Value = true);
            AddAssert("rate forced", () => playback.ChartModTempo.Value == 1.5);

            AddStep("a replay starts playing", () => jukebox.NowPlaying.Value = new BeatmapSetInfo
            {
                Id = 1,
                Replay = new ReplayAttachment { PlayerName = "Cookiezi", ModAcronyms = new[] { "HD", "HR" } },
            });

            AddAssert("selection reports the replay is in control", () => selection.ReplayActive.Value);
            AddAssert("and its own rate stood down",
                () => playback.ChartModTempo.Value == 1 && playback.ChartModFrequency.Value == 1);
            AddAssert("the replay's mods are what it reports",
                () => selection.ReplayModAcronyms.Value.SequenceEqual(new[] { "HD", "HR" }));

            AddAssert("the user's own selection was left intact underneath",
                () => selection.Enabled(ChartMod.DoubleTime).Value);

            AddStep("replay stops", () => jukebox.NowPlaying.Value = new BeatmapSetInfo { Id = 2 });

            AddAssert("the selection's rate comes back", () => playback.ChartModTempo.Value == 1.5);
        }

        [Test]
        public void SelectionRoundTripsThroughConfig()
        {
            AddStep("enable HD + HR + DT", () =>
            {
                selection.Enabled(ChartMod.Hidden).Value = true;
                selection.Enabled(ChartMod.HardRock).Value = true;
                selection.Enabled(ChartMod.DoubleTime).Value = true;
            });

            AddAssert("persisted as acronyms", () => config.Get<string>(JukeBoxSetting.ChartMods) == "HR,HD,DT");

            AddStep("a config written by something else lands", () => config.SetValue(JukeBoxSetting.ChartMods, "EZ,FL"));

            AddAssert("the selection followed it",
                () => selection.Selected.SequenceEqual(new[] { ChartMod.Easy, ChartMod.Flashlight }));

            // An unparseable/hand-edited value must not take the rest of the list down with it.
            AddStep("a config with junk in it lands", () => config.SetValue(JukeBoxSetting.ChartMods, "HD,NOTAMOD,FL"));

            AddAssert("the two real ones still applied",
                () => selection.Selected.SequenceEqual(new[] { ChartMod.Hidden, ChartMod.Flashlight }));

            // Two mods that can't coexist (only reachable by hand-editing) resolve in list order.
            AddStep("a config with an impossible pair lands", () => config.SetValue(JukeBoxSetting.ChartMods, "DT,HT"));

            AddAssert("only the first survived", () => selection.Selected.SequenceEqual(new[] { ChartMod.DoubleTime }));
        }

        /// <summary>
        /// Mods are stateful — <c>ModWithVisibilityAdjustment</c> (HD) binds config bindables when a
        /// DrawableRuleset loads, and binding an already-bound bindable throws. The chart layer is
        /// rebuilt constantly (difficulty switch, skin change, mod change), so every build must get
        /// its own instances. Same crash the replay path already guards against.
        /// </summary>
        [Test]
        public void EveryBuildGetsItsOwnModInstances()
        {
            AddStep("enable HD + HR", () =>
            {
                selection.Enabled(ChartMod.Hidden).Value = true;
                selection.Enabled(ChartMod.HardRock).Value = true;
            });

            var builds = new List<IReadOnlyList<Mod>>();

            AddStep("materialise the selection three times", () =>
            {
                var ruleset = new OsuRuleset();

                for (int i = 0; i < 3; i++)
                    builds.Add(selection.CreateFor(ruleset));
            });

            AddAssert("each build got the right mods",
                () => builds.All(b => b.Select(m => m.Acronym).SequenceEqual(new[] { "HR", "HD" })));

            AddAssert("no build shares an instance with another",
                () => builds.SelectMany(b => b).Distinct(ReferenceEqualityComparer.Instance).Count() == builds.Sum(b => b.Count));
        }

        /// <summary>
        /// The end of the chain: a selected mod has to reach the hosted DrawableRuleset AND the
        /// beatmap conversion behind it, riding alongside autoplay rather than replacing it. HR is
        /// the clearest proof — it mirrors the playfield vertically, which shows up in the converted
        /// beatmap's own coordinates rather than only in a mods list.
        /// </summary>
        [Test]
        public void SelectedModsReachTheHostedRulesetAndTheConvertedBeatmap()
        {
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            float unmoddedFirstY = 0;

            AddStep("build an unmodded chart to measure against", () =>
            {
                Directory.CreateDirectory(dir);
                buildLayer(dir);
            });

            AddUntilStep("unmodded layer loaded", () => layer.IsLoaded && layer.PlayableBeatmap != null);
            AddAssert("autoplay is driving it", () => layer.DrawableRuleset!.Mods.Any(m => m is ModAutoplay));
            AddAssert("and nothing else is", () => layer.DrawableRuleset!.Mods.Count() == 1);
            AddStep("record the first object's position", () => unmoddedFirstY = firstObjectY());

            AddStep("select HD + HR", () =>
            {
                selection.Enabled(ChartMod.Hidden).Value = true;
                selection.Enabled(ChartMod.HardRock).Value = true;
            });

            AddStep("rebuild the chart", () =>
            {
                layerHost.Clear();
                buildLayer(dir);
            });

            AddUntilStep("modded layer loaded", () => layer.IsLoaded && layer.PlayableBeatmap != null);

            AddAssert("both mods reached the ruleset, autoplay included",
                () => layer.DrawableRuleset!.Mods.Select(m => m.Acronym).ToHashSet().IsSupersetOf(new[] { "HD", "HR" })
                      && layer.DrawableRuleset!.Mods.Any(m => m is ModAutoplay));

            AddAssert("HR actually converted the beatmap (playfield mirrored)",
                () => Math.Abs(firstObjectY() - (384 - unmoddedFirstY)) < 1);

            AddStep("clean up", () => layerHost.Clear());
        }

        private LazerChartLayer layer = null!;

        private float firstObjectY()
            => ((osu.Game.Rulesets.Osu.Objects.OsuHitObject)layer.PlayableBeatmap!.HitObjects[0]).Position.Y;

        private void buildLayer(string dir)
        {
            manual.CurrentTime = 0;

            string osu = Path.Combine(dir, "mods [0].osu");

            File.WriteAllText(osu,
                "osu file format v14\n\n[General]\nAudioFilename: audio.wav\nMode: 0\n\n"
                + "[Metadata]\nTitle:test\nArtist:test\nCreator:test\nVersion:test\n\n"
                + "[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:5\nApproachRate:5\nSliderMultiplier:1.4\nSliderTickRate:1\n\n"
                + "[TimingPoints]\n0,500,4,1,0,100,1,0\n\n"
                + "[HitObjects]\n64,192,1000,1,0\n192,192,1500,1,0\n");

            layerHost.Child = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Clock = new FramedClock(manual),
                Child = layer = new LazerChartLayer(new osu.Game.Beatmaps.FlatWorkingBeatmap(osu), osu),
            };
        }

        /// <summary>Each ruleset supplies its OWN implementation of a mod (TaikoModHidden is not
        /// OsuModHidden), so the selection has to resolve through the ruleset it is building for.</summary>
        [Test]
        public void ModsResolveToTheRulesetsOwnImplementations()
        {
            AddStep("enable HD + DT", () =>
            {
                selection.Enabled(ChartMod.Hidden).Value = true;
                selection.Enabled(ChartMod.DoubleTime).Value = true;
            });

            foreach (Ruleset ruleset in new Ruleset[] { new OsuRuleset(), new TaikoRuleset(), new CatchRuleset(), new ManiaRuleset() })
            {
                var captured = ruleset;

                AddAssert($"{captured.ShortName} gets its own HD and DT", () =>
                {
                    var mods = selection.CreateFor(captured);

                    return mods.Select(m => m.Acronym).OrderBy(a => a).SequenceEqual(new[] { "DT", "HD" })
                           && mods.All(m => captured.CreateAllMods().Any(a => a.GetType() == m.GetType()));
                });
            }
        }
    }
}
