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
                playback.Current.Value = null;
                playback.SelectedOsuFile.Value = null;
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

        /// <summary>
        /// The user's report: watching a Double Time replay and turning DT off left the track still
        /// running at 1.5×. A replay's frames are timestamped in beatmap time and everything here
        /// runs off one clock, so the rate is the user's to change — the play simply runs slower,
        /// still in sync. The replay's recorded rate is a separate track adjustment that multiplies
        /// with ours, so what is asserted is the PRODUCT the track actually ends up at.
        /// </summary>
        [Test]
        public void RateModsStayEditableDuringAReplay()
        {
            AddStep("a DT replay starts playing", () =>
            {
                // Exactly what Jukebox does for a replay round: the recorded rate onto the replay
                // channel, then the now-playing item the selection follows.
                playback.ReplayTempo.Value = 1.5;
                playback.ReplayFrequency.Value = 1.0;

                jukebox.NowPlaying.Value = new BeatmapSetInfo
                {
                    Id = 21,
                    Replay = new ReplayAttachment { PlayerName = "Cookiezi", ModAcronyms = new[] { "HD", "DT" } },
                };
            });

            AddUntilStep("the replay's mods are on the toggles", () => selection.Enabled(ChartMod.DoubleTime).Value);
            AddAssert("and the track runs at the recorded 1.5x", () => Math.Abs(trackRate() - 1.5) < 1e-9);

            AddStep("the user turns DT off", () => selection.Enabled(ChartMod.DoubleTime).Value = false);
            AddAssert("the track drops to 1x", () => Math.Abs(trackRate() - 1.0) < 1e-9);

            AddStep("the user asks for Half Time instead", () => selection.Enabled(ChartMod.HalfTime).Value = true);
            AddAssert("the track slows to 0.75x", () => Math.Abs(trackRate() - 0.75) < 1e-9);

            AddStep("and Nightcore, which shifts pitch instead", () => selection.Enabled(ChartMod.Nightcore).Value = true);
            AddAssert("the speed is carried by frequency now", () =>
                Math.Abs(playback.ChartModTempo.Value * playback.ReplayTempo.Value - 1.0) < 1e-9
                && Math.Abs(playback.ChartModFrequency.Value * playback.ReplayFrequency.Value - 1.5) < 1e-9);

            AddStep("replay stops", () =>
            {
                jukebox.NowPlaying.Value = new BeatmapSetInfo { Id = 22 };
                playback.ReplayTempo.Value = 1;
                playback.ReplayFrequency.Value = 1;
            });

            AddAssert("back to no forced rate", () => Math.Abs(trackRate() - 1.0) < 1e-9);
        }

        /// <summary>The rate the track actually ends up at: the replay's own adjustment and the
        /// selection's are separate, and the track multiplies them.</summary>
        private double trackRate()
            => playback.ChartModTempo.Value * playback.ReplayTempo.Value
               * playback.ChartModFrequency.Value * playback.ReplayFrequency.Value;

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
        /// A replay is a record of a play that already happened at a rate of its own, so the
        /// selection must not additionally speed the track up while one is watched — the replay's
        /// own rate (ReplayTempo/ReplayFrequency, set by <see cref="Jukebox"/>) is the only one in
        /// force. That holds even though the replay's mods are now applied to the toggles and left
        /// editable: the frames are timed against the rate they were recorded at, so following an
        /// edited rate mod would desync the play rather than re-render it.
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

            // The replay's mods become the ACTIVE selection and stay editable; the user's own is
            // parked rather than overwritten, and comes back below.
            AddAssert("the replay's mods are on the toggles",
                () => selection.Enabled(ChartMod.Hidden).Value
                      && selection.Enabled(ChartMod.HardRock).Value
                      && !selection.Enabled(ChartMod.DoubleTime).Value);

            AddStep("replay stops", () => jukebox.NowPlaying.Value = new BeatmapSetInfo { Id = 2 });

            AddAssert("the user's own selection is handed back",
                () => selection.Enabled(ChartMod.DoubleTime).Value
                      && !selection.Enabled(ChartMod.Hidden).Value
                      && !selection.Enabled(ChartMod.HardRock).Value);
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

        // ---- osu!mania ----

        /// <summary>
        /// The nine key-count mods are driven by one checkbox plus a 1-9 value now, but the thing
        /// they drive is unchanged: exactly one <c>ManiaModKeyN</c>, resolved by acronym. Exclusivity
        /// is inherent to a single value — this is the proof that the collapse preserved it rather
        /// than the proof that lazer enforces it.
        /// </summary>
        [Test]
        public void OneKeyCountAtATimeAndNoneWhenUnticked()
        {
            AddStep("play a mania difficulty", () => playingMania());

            AddStep("select 4 keys", () => selection.Enabled(ChartMod.Key4).Value = true);
            AddAssert("only 4K is on", () => keyModsOn().SequenceEqual(new[] { ChartMod.Key4 }));

            AddStep("select 7 keys instead", () =>
            {
                selection.Enabled(ChartMod.Key4).Value = false;
                selection.Enabled(ChartMod.Key7).Value = true;
            });

            AddAssert("only 7K is on", () => keyModsOn().SequenceEqual(new[] { ChartMod.Key7 }));
            AddAssert("and it builds as 7K for mania",
                () => selection.CreateFor(new ManiaRuleset()).Single().Acronym == "7K");

            AddStep("turn the override off", () => selection.Enabled(ChartMod.Key7).Value = false);
            AddAssert("no key mod is on", () => keyModsOn().Count == 0);
            AddAssert("and mania builds none", () => selection.CreateFor(new ManiaRuleset()).Count == 0);
        }

        /// <summary>Every count osu!mania offers resolves to its own real mod — the catalogue's
        /// acronym mapping is what the collapsed control indexes into.</summary>
        [Test]
        public void EveryKeyCountFromOneToNineResolvesToItsOwnMod()
        {
            AddStep("play a mania difficulty", () => playingMania());

            for (int keys = ChartModCatalog.min_key_count; keys <= ChartModCatalog.max_key_count; keys++)
            {
                int captured = keys;

                AddStep($"select {captured} keys", () =>
                {
                    foreach (var m in ChartModCatalog.KeyCountMods)
                        selection.Enabled(m).Value = false;

                    selection.Enabled(ChartModCatalog.KeyCountMod(captured)!.Value).Value = true;
                });

                AddAssert($"mania builds {captured}K",
                    () => selection.CreateFor(new ManiaRuleset()).Single().Acronym == $"{captured}K");
            }

            AddAssert("and nothing outside 1-9 is offered at all",
                () => ChartModCatalog.KeyCountMod(0) == null && ChartModCatalog.KeyCountMod(10) == null);
        }

        private IReadOnlyList<ChartMod> keyModsOn()
            => ChartModCatalog.KeyCountMods.Where(m => selection.Enabled(m).Value).ToArray();

        /// <summary>
        /// Fade In makes the notes appear late where Hidden makes them vanish early, so osu!mania
        /// refuses to run both — <c>ManiaModFadeIn</c> names <c>ManiaModHidden</c> among its
        /// exclusions. Co-op, by contrast, excludes NOTHING in lazer: it doubles the stage and is
        /// meant to be combined, key mods included.
        /// </summary>
        [Test]
        public void FadeInFightsHiddenButCoopFightsNothing()
        {
            AddStep("play a mania difficulty", () => playingMania());

            AddStep("select HD", () => selection.Enabled(ChartMod.Hidden).Value = true);
            AddStep("select Fade In", () => selection.Enabled(ChartMod.FadeIn).Value = true);

            AddAssert("Fade In on", () => selection.Enabled(ChartMod.FadeIn).Value);
            AddAssert("HD switched itself off", () => !selection.Enabled(ChartMod.Hidden).Value);

            AddStep("select Co-op and 7K on top", () =>
            {
                selection.Enabled(ChartMod.DualStages).Value = true;
                selection.Enabled(ChartMod.Key7).Value = true;
            });

            AddAssert("Co-op, 7K and Fade In all coexist",
                () => selection.Enabled(ChartMod.DualStages).Value
                      && selection.Enabled(ChartMod.Key7).Value
                      && selection.Enabled(ChartMod.FadeIn).Value);
        }

        /// <summary>
        /// The rules are genuinely per-ruleset, and this is the pair that proves it: osu! is happy
        /// with HDFL (an ordinary play) while osu!mania forbids it, because ManiaModHidden lists
        /// ModFlashlight among its exclusions and OsuModHidden does not. A single global answer
        /// would be wrong in one direction or the other.
        /// </summary>
        [Test]
        public void HiddenAndFlashlightCoexistInOsuButNotInMania()
        {
            AddStep("play an osu! difficulty", () => playingOsu());

            AddStep("select HD + FL", () =>
            {
                selection.Enabled(ChartMod.Hidden).Value = true;
                selection.Enabled(ChartMod.Flashlight).Value = true;
            });

            AddAssert("osu! keeps both",
                () => selection.Enabled(ChartMod.Hidden).Value && selection.Enabled(ChartMod.Flashlight).Value);

            // Even carried into a mania chart, the pair must not be BUILT there.
            AddAssert("but a mania chart is only handed one of them",
                () => selection.CreateFor(new ManiaRuleset()).Count(m => m.Acronym is "HD" or "FL") == 1);

            AddStep("switch to a mania difficulty", () => playingMania());

            AddAssert("mania calls the pair incompatible",
                () => !selection.Compatible(ChartMod.Hidden, ChartMod.Flashlight));

            // Re-picking FL there resolves it the way clicking it would (a bindable already at true
            // fires nothing, so it has to actually move).
            AddStep("re-pick FL under mania", () =>
            {
                selection.Enabled(ChartMod.Flashlight).Value = false;
                selection.Enabled(ChartMod.Flashlight).Value = true;
            });

            AddAssert("mania switched HD off", () => !selection.Enabled(ChartMod.Hidden).Value);
        }

        /// <summary>A mania-only selection must never be applied to another ruleset's chart —
        /// resolution is by acronym, and osu! simply has no "7K".</summary>
        [Test]
        public void ManiaOnlyModsNeverReachAnotherRulesetsChart()
        {
            AddStep("play a mania difficulty", () => playingMania());

            AddStep("select 7K + Co-op + Fade In", () =>
            {
                selection.Enabled(ChartMod.Key7).Value = true;
                selection.Enabled(ChartMod.DualStages).Value = true;
                selection.Enabled(ChartMod.FadeIn).Value = true;
            });

            AddAssert("mania builds all three",
                () => selection.CreateFor(new ManiaRuleset()).Select(m => m.Acronym).OrderBy(a => a)
                               .SequenceEqual(new[] { "7K", "DS", "FI" }));

            foreach (Ruleset ruleset in new Ruleset[] { new OsuRuleset(), new TaikoRuleset(), new CatchRuleset() })
            {
                var captured = ruleset;
                AddAssert($"{captured.ShortName} builds none of them", () => selection.CreateFor(captured).Count == 0);
            }

            AddAssert("and the selection itself was left intact",
                () => selection.Enabled(ChartMod.Key7).Value && selection.Enabled(ChartMod.DualStages).Value);
        }

        /// <summary>
        /// osu!'s own rule, measured rather than assumed: the key counts and Co-op reach the
        /// beatmap only through <c>IApplicableToBeatmapConverter</c>, and mania's converter ignores
        /// a requested column count for a map that is ALREADY mania. Stable behaves the same way.
        ///
        /// <para>
        /// This app always renders a beatmap in the ruleset its own .osu declares, so a convert
        /// never happens and these mods can never bite here. They still reach the ruleset's mod
        /// list — nothing is filtered out behind the user's back — but the column count stays put,
        /// which is why the Chart tab greys the rows and says so.
        /// </para>
        /// </summary>
        [Test]
        public void KeyModsAndCoopOnlyEverApplyToConvertedBeatmaps()
        {
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            AddAssert("the catalogue knows they are converts-only",
                () => ChartModCatalog.AppliesOnlyToConverts(ChartMod.Key7)
                      && ChartModCatalog.AppliesOnlyToConverts(ChartMod.DualStages));

            AddAssert("and that Mirror and Random are not",
                () => !ChartModCatalog.AppliesOnlyToConverts(ChartMod.Mirror)
                      && !ChartModCatalog.AppliesOnlyToConverts(ChartMod.Random));

            AddStep("build an unmodded 4K chart", () =>
            {
                Directory.CreateDirectory(dir);
                playingMania();
                buildManiaLayer(dir);
            });

            AddUntilStep("layer loaded", () => layer.IsLoaded && layer.PlayableBeatmap != null);
            AddAssert("the map is 4 columns in one stage",
                () => maniaBeatmap().TotalColumns == 4 && maniaBeatmap().Stages.Count == 1);

            AddStep("select 7 keys + Co-op", () =>
            {
                selection.Enabled(ChartMod.Key7).Value = true;
                selection.Enabled(ChartMod.DualStages).Value = true;
            });

            AddStep("rebuild", () =>
            {
                layerHost.Clear();
                buildManiaLayer(dir);
            });

            AddUntilStep("modded layer loaded", () => layer.IsLoaded && layer.PlayableBeatmap != null);

            AddAssert("both still reached the ruleset's mod list",
                () => layer.DrawableRuleset!.Mods.Select(m => m.Acronym).ToHashSet().IsSupersetOf(new[] { "7K", "DS" }));

            AddAssert("but a native mania map keeps its own stage",
                () => maniaBeatmap().TotalColumns == 4 && maniaBeatmap().Stages.Count == 1);

            AddStep("clean up", () => layerHost.Clear());
        }

        /// <summary>Mirror and Random reorder the columns rather than adding any, so they are proven
        /// by reaching the ruleset and by Mirror actually flipping the converted note positions.</summary>
        [Test]
        public void MirrorReachesTheRulesetAndFlipsTheConvertedColumns()
        {
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            int[] unmoddedColumns = Array.Empty<int>();

            AddStep("build an unmodded 4K chart", () =>
            {
                Directory.CreateDirectory(dir);
                playingMania();
                buildManiaLayer(dir);
            });

            AddUntilStep("layer loaded", () => layer.IsLoaded && layer.PlayableBeatmap != null);
            AddStep("record the columns", () => unmoddedColumns = columns());

            AddStep("select Mirror", () => selection.Enabled(ChartMod.Mirror).Value = true);
            AddStep("rebuild", () =>
            {
                layerHost.Clear();
                buildManiaLayer(dir);
            });

            AddUntilStep("mirrored layer loaded", () => layer.IsLoaded && layer.PlayableBeatmap != null);

            AddAssert("Mirror reached the ruleset", () => layer.DrawableRuleset!.Mods.Any(m => m.Acronym == "MR"));
            AddAssert("and every note moved to its mirrored column",
                () => columns().SequenceEqual(unmoddedColumns.Select(c => maniaBeatmap().TotalColumns - 1 - c)));

            AddStep("clean up", () => layerHost.Clear());
        }

        [Test]
        public void ManiaSelectionRoundTripsThroughConfig()
        {
            AddStep("play a mania difficulty", () => playingMania());

            AddStep("select 7K + Co-op + Mirror", () =>
            {
                selection.Enabled(ChartMod.Key7).Value = true;
                selection.Enabled(ChartMod.DualStages).Value = true;
                selection.Enabled(ChartMod.Mirror).Value = true;
            });

            AddAssert("persisted as acronyms", () => config.Get<string>(JukeBoxSetting.ChartMods) == "7K,DS,MR");

            AddStep("a config written by something else lands", () => config.SetValue(JukeBoxSetting.ChartMods, "FI,3K"));

            AddAssert("the selection followed it",
                () => selection.Selected.SequenceEqual(new[] { ChartMod.FadeIn, ChartMod.Key3 }));
        }

        private osu.Game.Rulesets.Mania.Beatmaps.ManiaBeatmap maniaBeatmap()
            => (osu.Game.Rulesets.Mania.Beatmaps.ManiaBeatmap)layer.PlayableBeatmap!;

        private int[] columns()
            => layer.PlayableBeatmap!.HitObjects
                    .OfType<osu.Game.Rulesets.Mania.Objects.ManiaHitObject>()
                    .Select(h => h.Column)
                    .ToArray();

        private void buildManiaLayer(string dir)
        {
            manual.CurrentTime = 0;

            string osu = Path.Combine(dir, "mods [3].osu");

            // CircleSize is the column count in mania, so this is a 4K map. The x positions place
            // one note in each of the four columns (128 units apart across a 512-wide playfield).
            File.WriteAllText(osu,
                "osu file format v14\n\n[General]\nAudioFilename: audio.wav\nMode: 3\n\n"
                + "[Metadata]\nTitle:test\nArtist:test\nCreator:test\nVersion:test\n\n"
                + "[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:5\nApproachRate:5\nSliderMultiplier:1.4\nSliderTickRate:1\n\n"
                + "[TimingPoints]\n0,500,4,1,0,100,1,0\n\n"
                + "[HitObjects]\n64,192,1000,1,0\n192,192,1500,1,0\n320,192,2000,1,0\n448,192,2500,1,0\n");

            layerHost.Child = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Clock = new FramedClock(manual),
                Child = layer = new LazerChartLayer(new osu.Game.Beatmaps.FlatWorkingBeatmap(osu), osu),
            };
        }

        /// <summary>Publishes a set whose selected difficulty is of the given mode — the selection
        /// reads its mod rules off whatever ruleset is on screen (see
        /// <see cref="ChartModSelection.CurrentRulesetId"/>).</summary>
        private void playing(int mode)
        {
            playback.Current.Value = new CachedBeatmapSet
            {
                Directory = "/set",
                PreferredOsuFile = $"/set/diff{mode}.osu",
                OsuFiles = new List<string> { $"/set/diff{mode}.osu" },
                Difficulties = new List<DifficultyInfo> { new DifficultyInfo { Path = $"/set/diff{mode}.osu", Version = $"mode {mode}", Mode = mode } },
            };

            playback.SelectedOsuFile.Value = $"/set/diff{mode}.osu";
        }

        private void playingMania() => playing(3);

        private void playingOsu() => playing(0);
    }
}
