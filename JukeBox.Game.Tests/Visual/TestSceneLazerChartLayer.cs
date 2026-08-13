#nullable enable

using System;
using System.IO;
using System.Linq;
using JukeBox.Game.LazerPlayer;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Timing;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Taiko;

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

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
        }

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

        private void createLayer(int mode)
        {
            AddStep("create layer", () =>
            {
                manual.CurrentTime = 0;

                string osu = Path.Combine(dir, $"test [{mode}].osu");
                File.WriteAllText(osu, beatmapForMode(mode));

                Add(host = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = new FramedClock(manual),
                    Child = layer = new LazerChartLayer(new FlatWorkingBeatmap(osu), osu),
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
