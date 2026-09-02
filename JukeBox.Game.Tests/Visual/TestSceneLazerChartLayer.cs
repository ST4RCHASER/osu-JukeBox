#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Replays;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Replays;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Taiko;
using osu.Game.Scoring;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The lazer gameplay layer hosts a real osu!lazer DrawableRuleset for every ruleset: loads a
    /// real decoded .osu, drives it from a manual clock (the same external-clock arrangement the
    /// playback clock uses in production), and checks the playfield actually populates with
    /// drawable hit objects. Deep gameplay assertions are intentionally absent — lazer tests its
    /// own rulesets; these only cover our hosting/DI arrangement.
    /// </summary>
    [TestFixture]
    public partial class TestSceneLazerChartLayer : JukeBoxTestScene
    {
        private readonly ManualClock manual = new ManualClock();

        private string dir = null!;
        private Container host = null!;
        private LazerChartLayer layer = null!;

        /// <summary>The runner is a real JukeBoxGameBase, so the mod selection and the jukebox it
        /// follows are the app's own — which is what lets a replay be started here the way the app
        /// starts one.</summary>
        [osu.Framework.Allocation.Resolved]
        private ChartModSelection chartMods { get; set; } = null!;

        [osu.Framework.Allocation.Resolved]
        private JukeBox.Game.Playback.Jukebox jukebox { get; set; } = null!;

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
        }

        // The jukebox and the mod selection are the runner's, shared with every other fixture, and a
        // replay left in the now-playing slot is what makes the selection the source of a replay
        // layer's mods (see LazerChartLayer.replayMods). Cleared on the way IN as well as out, so
        // this fixture never depends on which one ran before it.
        [SetUpSteps]
        public void SetUpSteps() => clearReplay();

        [TearDownSteps]
        public void TearDownSteps() => clearReplay();

        private void clearReplay() => AddStep("no replay playing", () => jukebox.NowPlaying.Value = null);

        [Test]
        public void OsuLayerRendersGameplay() => runForMode(0, typeof(OsuRuleset));

        // The osu! replay-analysis overlay (click markers / cursor path / frame markers) attaches
        // to the hosted DrawableRuleset exactly as lazer's ReplayPlayer path does — driven by the
        // autoplay replay, config-bound to the real OsuRulesetConfigManager keys.
        [Test]
        public void OsuLayerAttachesReplayAnalysisOverlay()
        {
            createLayer(0);
            AddUntilStep("layer loaded", () => layer.IsLoaded && layer.DrawableRuleset != null);
            AddUntilStep("analysis overlay attached", () => layer.HasAnalysisOverlay);
        }

        [Test]
        public void TaikoLayerRendersGameplay() => runForMode(1, typeof(TaikoRuleset));

        [Test]
        public void CatchLayerRendersGameplay() => runForMode(2, typeof(CatchRuleset));

        [Test]
        public void ManiaLayerRendersGameplay() => runForMode(3, typeof(ManiaRuleset));

        // Regression guard for the big-seek crawl: FrameStabilityContainer's frame-stable catch-up
        // only advances a bounded slice of gameplay time per (wall-clock-budgeted) real frame, so
        // a 30s scrub would otherwise fast-forward visibly. LazerChartLayer snaps (lazer's own
        // non-frame-stable seek pattern) on jumps beyond its threshold. Assertions target the
        // MECHANISM, not racy frame-count comparisons (headless catch-up speed varies with
        // scheduling): snap=true must actually engage the internal FrameStablePlayback reflection
        // hook (SeekSnapsEngaged — fails loudly if upstream renames the property) and catch up
        // near-instantly; snap=false must complete the seek WITHOUT the hook ever engaging.
        [TestCase(true)]
        [TestCase(false)]
        public void BigSeekSnapBehaviour(bool snap)
        {
            createLayer(0);

            AddStep("configure snap", () => layer.SnapOnBigSeeks = snap);
            AddUntilStep("layer loaded", () => layer.IsLoaded && layer.DrawableRuleset != null);
            AddUntilStep("settled at start", () => frameStableTimeNear(0));

            AddStep("seek +30s", () => manual.CurrentTime = 30000);
            AddUntilStep("caught up to 30s", () => layer.LastSeekCatchupFrames >= 0 && frameStableTimeNear(30000));

            AddStep("report catch-up cost", () => osu.Framework.Logging.Logger.Log(
                $"[seek-snap] snap={snap}: 30s seek caught up in {layer.LastSeekCatchupFrames} layer update(s), snaps engaged: {layer.SeekSnapsEngaged}"));

            if (snap)
            {
                AddAssert("snap hook engaged exactly once", () => layer.SeekSnapsEngaged == 1);
                AddAssert("snapped within 3 updates", () => layer.LastSeekCatchupFrames is >= 1 and <= 3);
            }
            else
                AddAssert("caught up without the snap hook", () => layer.SeekSnapsEngaged == 0);

            AddStep("remove layer", () => Remove(host, true));
        }

        // Regression guard for the construction-time crawl: a layer freshly attached with the
        // playback clock already mid-song (RenderChart toggled on mid-track, difficulty/skin
        // rebuilds) starts its FrameStabilityContainer and autoplay replay walk at the song start
        // and would visibly fast-forward to the current position. The first-update construction
        // snap must engage the same FrameStablePlayback hook and land at the current time within
        // a few updates.
        [TestCase(true)]
        [TestCase(false)]
        public void MidSongConstructionSnapBehaviour(bool snap)
        {
            createLayer(0, 60000, snap);

            AddUntilStep("layer loaded", () => layer.IsLoaded && layer.DrawableRuleset != null);
            AddUntilStep("caught up to 60s", () => layer.LastSeekCatchupFrames >= 0 && frameStableTimeNear(60000));

            AddStep("report catch-up cost", () => osu.Framework.Logging.Logger.Log(
                $"[attach-snap] snap={snap}: mid-song construction caught up in {layer.LastSeekCatchupFrames} layer update(s), snaps engaged: {layer.SeekSnapsEngaged}"));

            if (snap)
            {
                AddAssert("construction snap engaged exactly once", () => layer.SeekSnapsEngaged == 1);
                AddAssert("snapped within 3 updates", () => layer.LastSeekCatchupFrames is >= 1 and <= 3);
            }
            else
                AddAssert("caught up without the snap hook", () => layer.SeekSnapsEngaged == 0);

            AddStep("remove layer", () => Remove(host, true));
        }

        [Test]
        public void SeekingKeepsFrameStableClockFollowing()
        {
            createLayer(0);

            AddUntilStep("layer loaded", () => layer.IsLoaded && layer.DrawableRuleset != null);

            AddStep("seek mid-song", () => manual.CurrentTime = 2000);
            AddUntilStep("frame-stable clock caught up", () => frameStableTimeNear(2000));

            AddStep("seek backwards", () => manual.CurrentTime = 500);
            AddUntilStep("frame-stable clock followed the rewind", () => frameStableTimeNear(500));

            AddStep("remove layer", () => Remove(host, true));
        }

        // A set queued from a dropped .osr must render the REAL play, not autoplay: the layer hands
        // the decoded score straight to DrawableRuleset.SetReplayScore, so the cursor follows the
        // frames the player actually produced.
        [Test]
        public void ADroppedReplayDrivesGameplayInsteadOfAutoplay()
        {
            Score replay = null!;

            AddStep("create layer with a replay", () =>
            {
                manual.CurrentTime = 0;

                string osu = Path.Combine(dir, "replayed [0].osu");
                File.WriteAllText(osu, beatmapForMode(0));

                string osr = Path.Combine(dir, "replayed.osr");
                Import.ReplayFixture.Write(osr, osu, "Cookiezi");
                replay = new JukeBoxScoreDecoder(osu).Decode(osr);

                Add(host = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = new FramedClock(manual),
                    Child = layer = new LazerChartLayer(new FlatWorkingBeatmap(osu), osu, replay),
                });
            });

            AddUntilStep("layer loaded", () => layer.IsLoaded && layer.DrawableRuleset != null);

            AddAssert("the user's replay is what's driving it", () => layer.UsingUserReplay);
            AddAssert("and it is the score the ruleset was given", () => ReferenceEquals(layer.DrawableRuleset!.ReplayScore, replay));
            AddAssert("no autoplay mod was applied", () => layer.DrawableRuleset!.Mods.All(m => m is not ModAutoplay));

            // The overlay still attaches, now drawing the real play's cursor path rather than a
            // generated one.
            AddUntilStep("analysis overlay attached", () => layer.HasAnalysisOverlay);

            AddStep("remove layer", () => Remove(host, true));
        }

        // A replay's mods have to reach the ruleset, or the cursor is replaying a play that
        // happened on a different-looking beatmap than the one being drawn. HR is the clearest
        // proof: it mirrors the playfield vertically, so its effect is visible in the converted
        // beatmap's own coordinates rather than only in a mods list.
        [Test]
        public void AReplaysModsReachTheRulesetAndTheConvertedBeatmap()
        {
            float unmoddedFirstY = 0;

            AddStep("build an unmodded layer to measure against", () =>
            {
                manual.CurrentTime = 0;
                string osu = Path.Combine(dir, "plain [0].osu");
                File.WriteAllText(osu, beatmapForMode(0));

                Add(host = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = new FramedClock(manual),
                    Child = layer = new LazerChartLayer(new FlatWorkingBeatmap(osu), osu),
                });
            });

            AddUntilStep("unmodded layer loaded", () => layer.IsLoaded && layer.PlayableBeatmap != null);
            AddStep("record the first object's position", () => unmoddedFirstY = firstObjectY());
            AddStep("remove layer", () => Remove(host, true));

            AddStep("build a layer from an HD/HR/DT replay", () =>
            {
                manual.CurrentTime = 0;
                string osu = Path.Combine(dir, "modded [0].osu");
                File.WriteAllText(osu, beatmapForMode(0));

                var ruleset = LazerChartLayer.CreateRuleset(0);

                // 8 | 16 | 64 = HD | HR | DT, the mod bitfield of the real replay this was verified
                // against (Cookiezi, masterpiece [Insane], 2013-11-10).
                var score = new Score
                {
                    Replay = new Replay { Frames = { new OsuReplayFrame(0, new osuTK.Vector2(64, 96)) } },
                    ScoreInfo = new ScoreInfo
                    {
                        Ruleset = ruleset.RulesetInfo,
                        Mods = ruleset.ConvertFromLegacyMods((LegacyMods)(8 | 16 | 64)).ToArray(),
                    },
                };

                Add(host = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = new FramedClock(manual),
                    Child = layer = new LazerChartLayer(new FlatWorkingBeatmap(osu), osu, score),
                });
            });

            AddUntilStep("modded layer loaded", () => layer.IsLoaded && layer.DrawableRuleset != null);

            AddAssert("every replay mod reached the ruleset",
                () => layer.DrawableRuleset!.Mods.Select(m => m.Acronym).ToHashSet().IsSupersetOf(new[] { "HD", "HR", "DT" }));

            AddAssert("HR actually converted the beatmap (playfield mirrored)",
                () => Math.Abs(firstObjectY() - (384 - unmoddedFirstY)) < 1);

            AddAssert("the rate mod is carried but changes no gameplay time here",
                () => ReplayMods.RateFor(layer.DrawableRuleset!.Mods) == 1.5
                      && layer.PlayableBeatmap!.HitObjects[0].StartTime == 1000);

            AddStep("remove layer", () => Remove(host, true));
        }

        /// <summary>
        /// A replay's mods are applied to the Chart tab's toggles and stay editable there (user
        /// request), and what the toggles say is what the ruleset is built with — so turning Hard
        /// Rock off during a replay really does render the chart without it. The replay's own frames
        /// are unchanged by that, which is the trade the user is making.
        /// </summary>
        [Test]
        public void EditingModsDuringAReplayReachesTheRuleset()
        {
            Score score = null!;
            string osu = null!;

            AddStep("a replay is driving playback", () =>
            {
                manual.CurrentTime = 0;
                osu = Path.Combine(dir, "editable [0].osu");
                File.WriteAllText(osu, beatmapForMode(0));

                var ruleset = LazerChartLayer.CreateRuleset(0);

                score = new Score
                {
                    Replay = new Replay { Frames = { new OsuReplayFrame(0, new osuTK.Vector2(64, 96)) } },
                    ScoreInfo = new ScoreInfo
                    {
                        Ruleset = ruleset.RulesetInfo,
                        Mods = ruleset.ConvertFromLegacyMods((LegacyMods)(8 | 16 | 64)).ToArray(),
                    },
                };

                // The now-playing item is what makes the selection take the replay on — the same
                // signal the real app publishes when a dropped .osr starts.
                jukebox.NowPlaying.Value = new JukeBox.Game.Online.BeatmapSetInfo
                {
                    Id = 4242,
                    Replay = new ReplayAttachment { PlayerName = "Cookiezi", ModAcronyms = new[] { "HD", "HR", "DT" } },
                };
            });

            AddUntilStep("the selection took the replay on", () => chartMods.ReplayActive.Value
                                                                   && chartMods.Enabled(ChartMod.HardRock).Value);

            AddStep("build the layer", () => Add(host = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Clock = new FramedClock(manual),
                Child = layer = new LazerChartLayer(new FlatWorkingBeatmap(osu), osu, score),
            }));

            AddUntilStep("layer loaded", () => layer.IsLoaded && layer.DrawableRuleset != null);
            AddAssert("built with the replay's own mods",
                () => layer.DrawableRuleset!.Mods.Select(m => m.Acronym).ToHashSet().IsSupersetOf(new[] { "HD", "HR", "DT" }));

            AddStep("the user turns Hard Rock off", () => chartMods.Enabled(ChartMod.HardRock).Value = false);
            AddStep("rebuild the layer", () =>
            {
                Remove(host, true);

                Add(host = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = new FramedClock(manual),
                    Child = layer = new LazerChartLayer(new FlatWorkingBeatmap(osu), osu, score),
                });
            });

            AddUntilStep("rebuilt", () => layer.IsLoaded && layer.DrawableRuleset != null);
            AddAssert("Hard Rock is gone and the rest stayed", () =>
            {
                var acronyms = layer.DrawableRuleset!.Mods.Select(m => m.Acronym).ToHashSet();
                return !acronyms.Contains("HR") && acronyms.IsSupersetOf(new[] { "HD", "DT" });
            });

            AddStep("clean up", () =>
            {
                Remove(host, true);
                jukebox.NowPlaying.Value = null;
            });
        }

        // Regression guard for a real crash: the chart layer is rebuilt often (difficulty switch,
        // skin change, toggling the chart off and on), and every rebuild handed the SAME decoded
        // mod instances — stored once on the queue item — to a brand new DrawableRuleset.
        // ModWithVisibilityAdjustment (HD and friends) binds config bindables in ReadFromConfig,
        // and binding an already-bound bindable throws, so the second build aborted the app with
        // "An already bound bindable cannot be bound again."
        [Test]
        public void RebuildingWithTheSameReplayDoesNotReuseModInstances()
        {
            Score replay = null!;
            string osu = null!;

            AddStep("decode a replay with a config-binding mod (HD)", () =>
            {
                manual.CurrentTime = 0;

                osu = Path.Combine(dir, "rebuilt [0].osu");
                File.WriteAllText(osu, beatmapForMode(0));

                var ruleset = LazerChartLayer.CreateRuleset(0);

                replay = new Score
                {
                    Replay = new Replay { Frames = { new OsuReplayFrame(0, new osuTK.Vector2(64, 96)) } },
                    ScoreInfo = new ScoreInfo
                    {
                        Ruleset = ruleset.RulesetInfo,
                        Mods = ruleset.ConvertFromLegacyMods((LegacyMods)(8 | 16 | 64)).ToArray(),
                    },
                };
            });

            var modsSeenByEachBuild = new List<Mod[]>();

            // Three builds off the one decoded score, the way a diff switch then a skin change
            // would: the crash landed on the second.
            for (int build = 1; build <= 3; build++)
            {
                int number = build;

                AddStep($"build chart layer #{number}", () => Add(host = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = new FramedClock(manual),
                    Child = layer = new LazerChartLayer(new FlatWorkingBeatmap(osu), osu, replay),
                }));

                AddUntilStep($"layer #{number} loaded and rendering", () => layer.IsLoaded && layer.DrawableRuleset != null);

                AddStep($"record layer #{number}'s mods", () => modsSeenByEachBuild.Add(layer.DrawableRuleset!.Mods.ToArray()));

                AddAssert($"layer #{number} kept every replay mod", () => layer.DrawableRuleset!.Mods.Select(m => m.Acronym).ToHashSet()
                                                                              .SetEquals(replay.ScoreInfo.Mods.Select(m => m.Acronym)));
                AddAssert($"layer #{number} is replay-driven", () => layer.UsingUserReplay);

                AddStep($"remove layer #{number}", () => Remove(host, true));
            }

            // The mods are equal in every way that matters and different in the one that caused the
            // crash: identity. Each build must get its own instances, and none of them may be the
            // stored score's — those are the master copy that outlives every rebuild.
            AddAssert("no build reused another build's instances", () =>
                modsSeenByEachBuild.SelectMany(m => m).Distinct(ReferenceEqualityComparer.Instance).Count()
                == modsSeenByEachBuild.Sum(m => m.Length));

            AddAssert("no build was handed the stored score's own instances", () =>
                !modsSeenByEachBuild.SelectMany(m => m).Any(handed => replay.ScoreInfo.Mods.Any(stored => ReferenceEquals(handed, stored))));
        }

        private float firstObjectY()
            => ((osu.Game.Rulesets.Osu.Objects.OsuHitObject)layer.PlayableBeatmap!.HitObjects[0]).Position.Y;

        // The fallback has to stay exactly as it was: no replay means the generated autoplay mod
        // drives gameplay, same as before drag-and-drop existed.
        [Test]
        public void WithNoReplayGameplayStaysAutoplayDriven()
        {
            createLayer(0);

            AddUntilStep("layer loaded", () => layer.IsLoaded && layer.DrawableRuleset != null);
            AddAssert("not replay-driven", () => !layer.UsingUserReplay);
            AddAssert("autoplay mod applied", () => layer.DrawableRuleset!.Mods.Any(m => m is ModAutoplay));
            AddAssert("a generated replay is still attached", () => layer.DrawableRuleset!.ReplayScore?.Replay?.Frames.Count > 0);

            AddStep("remove layer", () => Remove(host, true));
        }

        private bool frameStableTimeNear(double target)
        {
            var clock = layer.DrawableRuleset?.FrameStableClock;
            return clock != null && !clock.IsCatchingUp.Value && Math.Abs(clock.CurrentTime - target) < 100;
        }

        private void runForMode(int mode, Type expectedRuleset)
        {
            createLayer(mode);

            AddUntilStep("layer loaded", () => layer.IsLoaded);
            AddAssert("correct ruleset", () => layer.Ruleset?.GetType() == expectedRuleset);
            AddAssert("drawable ruleset present", () => layer.DrawableRuleset != null);
            AddAssert("playable beatmap has objects", () => layer.ObjectCount > 0);

            AddStep("advance to first object", () => manual.CurrentTime = 1000);
            AddUntilStep("playfield populated", () => layer.DrawableRuleset!.Playfield.AllHitObjects.Any());

            AddStep("remove layer", () => Remove(host, true));
        }

        private void createLayer(int mode, double startTime = 0, bool snap = true)
        {
            AddStep("create layer", () =>
            {
                manual.CurrentTime = startTime;

                string osu = Path.Combine(dir, $"test [{mode}].osu");
                File.WriteAllText(osu, beatmapForMode(mode));

                Add(host = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = new FramedClock(manual),
                    // Snap configured at construction: the first Update (which owns the
                    // construction-snap decision) can run before any later test step.
                    Child = layer = new LazerChartLayer(new FlatWorkingBeatmap(osu), osu) { SnapOnBigSeeks = snap },
                });
            });
        }

        private static string beatmapForMode(int mode) =>
            "osu file format v14\n\n" +
            "[General]\nAudioFilename: audio.wav\nMode: " + mode + "\n\n" +
            "[Metadata]\nTitle:test\nArtist:test\nCreator:test\nVersion:test\n\n" +
            "[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:5\nApproachRate:5\nSliderMultiplier:1.4\nSliderTickRate:1\n\n" +
            "[TimingPoints]\n0,500,4,1,0,100,1,0\n\n" +
            "[HitObjects]\n" +
            "64,192,1000,1,0\n" +
            "192,192,1500,1,8\n" +
            (mode == 3 ? "448,192,2000,128,0,3000:0:0:0:0:\n" : "256,192,2000,1,0\n");
    }
}
