#nullable enable

using System;
using System.IO;
using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Playback;
using JukeBox.Game.Screens;
using JukeBox.Game.Storyboard;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Screens;
using osu.Framework.Testing;
using osu.Framework.Timing;

namespace JukeBox.Game.Tests.Visual
{
    [TestFixture]
    public partial class TestSceneBeatmapVisuals : JukeBoxTestScene
    {
        // Real (unpumped-by-us) IFrameBasedClock: PlaybackController normally owns pumping its
        // clock, but here we only need BeatmapVisuals to *load*, not animate, so a clock that
        // never advances is enough.
        private readonly FramedClock playbackClock = new FramedClock(new ManualClock());

        [Cached]
        private readonly PlaybackController controller = new PlaybackController();

        private string tmp = null!;
        private CachedBeatmapSet fixtureSetA = null!;
        private CachedBeatmapSet fixtureSetB = null!;

        public TestSceneBeatmapVisuals()
        {
            Add(controller);
        }

        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            string bgFile = Path.Combine(tmp, "bg.png");
            File.WriteAllBytes(bgFile, solidPng());

            fixtureSetA = new CachedBeatmapSet
            {
                SetId = 1,
                Directory = tmp,
                BackgroundFile = bgFile,
            };

            fixtureSetB = new CachedBeatmapSet
            {
                SetId = 2,
                Directory = tmp,
                BackgroundFile = bgFile,
            };
        }

        // NOTE: deliberately NOT deleting `tmp` here — see TestScenePlaybackController for why
        // (TestScene runs queued AddStep bodies from a base-class teardown hook that fires after
        // this derived class's own [TearDown], so a synchronous delete here would race the
        // fixture files out from under still-pending steps).

        [Test]
        public void VisualsLoadWithBackgroundOnly()
        {
            BeatmapVisuals visuals = null!;

            AddStep("create visuals", () => Add(visuals = new BeatmapVisuals(fixtureSetA, playbackClock)
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddUntilStep("visuals loaded", () => visuals.IsLoaded);
            AddAssert("background visible (no video/storyboard)", () => visuals.BackgroundVisible);

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        // Regression test for the "background auto-hides under video/storyboard" requirement: a
        // set with a non-empty storyboard must hide our own separate background sprite so the
        // storyboard renders as the top visual layer, matching real osu! behaviour.
        [Test]
        public void BackgroundHidesUnderNonEmptyStoryboard()
        {
            BeatmapVisuals visuals = null!;

            AddStep("create visuals with bg + storyboard", () =>
            {
                string osbFile = Path.Combine(tmp, "map.osb");
                File.WriteAllText(osbFile, """
                    osu file format v14

                    [Events]
                    //Storyboard Layer 0 (Background)
                    Sprite,Background,Centre,"bg.png",320,240
                    _F,0,0,5000,0,1
                    _M,0,0,5000,320,240,320,240
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 5,
                    Directory = tmp,
                    BackgroundFile = fixtureSetA.BackgroundFile,
                    OsbFile = osbFile,
                };

                Add(visuals = new BeatmapVisuals(set, playbackClock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("visuals loaded", () => visuals.IsLoaded);
            AddAssert("storyboard has objects", () => visuals.ChildrenOfType<TransformStoryboardLayer>().Single().HasObjects);
            AddAssert("background hidden", () => !visuals.BackgroundVisible);

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        [Test]
        public void SwappingCurrentSetSwapsVisualsAndDisposesOld()
        {
            NowPlayingScreen screen = null!;
            ScreenStack stack = null!;

            AddStep("clear current", () => controller.Current.Value = null);
            AddStep("create screen", () => Add(stack = new ScreenStack(screen = new NowPlayingScreen())
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddStep("set current to A", () => controller.Current.Value = fixtureSetA);
            AddUntilStep("visuals A loaded", () => screen.CurrentVisuals?.IsLoaded == true);

            BeatmapVisuals? firstVisuals = null;
            AddStep("capture first visuals", () => firstVisuals = screen.CurrentVisuals);

            AddStep("set current to B", () => controller.Current.Value = fixtureSetB);
            AddUntilStep("visuals B loaded (different instance)",
                () => screen.CurrentVisuals != null && screen.CurrentVisuals != firstVisuals && screen.CurrentVisuals.IsLoaded);

            AddAssert("old visuals disposed", () => firstVisuals!.Disposed);

            AddStep("remove screen", () => Remove(stack, true));
        }

        // Regression test for the "video layer must not sink the whole stack" requirement: a
        // corrupt/unsupported video file must not prevent BeatmapVisuals from loading with its
        // background + storyboard layers intact.
        [Test]
        public void CorruptVideoFileDoesNotPreventLoad()
        {
            BeatmapVisuals visuals = null!;

            AddStep("create visuals with garbage video", () =>
            {
                string videoFile = Path.Combine(tmp, "garbage.mp4");
                File.WriteAllBytes(videoFile, new byte[] { 0x00, 0x01, 0x02, 0x03 });

                var set = new CachedBeatmapSet
                {
                    SetId = 3,
                    Directory = tmp,
                    BackgroundFile = fixtureSetA.BackgroundFile,
                    VideoFile = videoFile,
                };

                Add(visuals = new BeatmapVisuals(set, playbackClock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("visuals loaded despite bad video", () => visuals.IsLoaded);

            // Not asserted here: the background being hidden while the (still-present) video
            // layer is up front — the decoder can fault fast enough that by this point the
            // teardown below has already happened, making that checkpoint inherently racy.

            // The garbage file doesn't fail Video's constructor synchronously — the decoder
            // faults asynchronously on its own thread a little later (confirmed empirically: the
            // runtime logs "VideoDecoder faulted: ... Invalid data found when processing input").
            // BeatmapVisuals.Update() polls Video.IsFaulted and tears the layer down once that
            // happens; assert that actually occurs rather than just that nothing crashed.
            AddUntilStep("video layer torn down after decoder fault", () => !visuals.HasVideoLayer);
            AddAssert("visuals still alive (no crash from the bad video)", () => !visuals.Disposed);
            AddAssert("background restored after fault teardown (no storyboard)", () => visuals.BackgroundVisible);

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        // Regression test for the crash-on-malformed-storyboard bug, exercised through the full
        // BeatmapVisuals stack (not just StoryboardLayer directly — see TestSceneStoryboardLayer
        // for the narrower version): a garbage .osb downloaded by Radio must not prevent the rest
        // of the visual stack (background) from loading, and must not crash BackgroundDependencyLoader.
        [Test]
        public void MalformedStoryboardDoesNotPreventVisualsFromLoading()
        {
            BeatmapVisuals visuals = null!;

            AddStep("create visuals with malformed storyboard", () =>
            {
                string osbFile = Path.Combine(tmp, "garbage.osb");
                File.WriteAllText(osbFile, """
                    osu file format v14

                    [Events]
                    Sprite,NotARealLayer,Centre,"bg.png",320,240
                    _M,0,0,5000,320,240,320,240
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 4,
                    Directory = tmp,
                    BackgroundFile = fixtureSetA.BackgroundFile,
                    OsbFile = osbFile,
                };

                Add(visuals = new BeatmapVisuals(set, playbackClock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("visuals loaded despite malformed storyboard", () => visuals.IsLoaded);
            AddAssert("no storyboard sprites visible (fell back to empty storyboard)",
                () => visuals.ChildrenOfType<TransformStoryboardLayer>().Single().VisibleSpriteCount == 0);

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        // Pumps a FramedOffsetClock off a real StopwatchClock so PlaybackPosition genuinely
        // advances in real time, like the production playbackClock (pumped by PlaybackController)
        // does — a frozen/manual clock wouldn't exercise Video's own per-frame catch-up logic.
        private partial class RealTimeClockPump : Component
        {
            public new readonly FramedOffsetClock Clock;

            public RealTimeClockPump(double startOffsetMs)
            {
                Clock = new FramedOffsetClock(new StopwatchClock(true), true) { Offset = startOffsetMs };
            }

            protected override void Update()
            {
                base.Update();
                Clock.ProcessFrame();
            }
        }

        // Regression test for the mid-song video seek-storm: a video-having set (re)built with
        // the clock already well past 0 — the normal case here, since radio songs start mid-track
        // and diff switches land mid-song — used to leave Video.Update()'s per-frame out-of-sync
        // check chasing a constantly-advancing PlaybackPosition target while the decoder was still
        // catching up, so it never actually landed a frame (permanent black video, re-seeking
        // every frame; see BeatmapVisuals.videoWarmedUp). The fixture video has a single keyframe
        // at t=0 covering its whole ~8s (forced via a large libx264 GOP), so seeking 6s in forces
        // exactly the deep decode-forward-through-the-GOP catch-up that triggered the storm.
        [Test]
        public void VideoCatchesUpWhenSeekedDeepIntoSongOnConstruction()
        {
            RealTimeClockPump pump = null!;
            BeatmapVisuals visuals = null!;

            AddStep("create real-time clock 6s into the song", () => Add(pump = new RealTimeClockPump(6000)));

            AddStep("create visuals with the single-keyframe fixture video", () =>
            {
                string videoFile = Path.Combine(tmp, "sync-test-video.mp4");
                File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "sync-test-video.mp4"), videoFile, true);

                var set = new CachedBeatmapSet
                {
                    SetId = 6,
                    Directory = tmp,
                    VideoFile = videoFile,
                };

                Add(visuals = new BeatmapVisuals(set, pump.Clock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("visuals loaded", () => visuals.IsLoaded);

            // Generous real-time bound: a healthy catch-up lands a synced frame within a couple of
            // hundred ms even decoding through a multi-second GOP (confirmed empirically against
            // several real cached maps) — 4s leaves comfortable headroom without the test itself
            // hanging on a genuine regression (a re-seek storm never produces a frame at all).
            AddUntilStep("video catches up and starts rendering synced frames",
                () => (visuals.VideoFramesProcessed ?? 0) > 0);

            // Regression for the residual-lag bug: freezing Video.IsPlaying during catch-up and
            // then simply flipping it back on resumes tracking from the stale frozen value with no
            // catch-up applied, baking the entire freeze duration in as a permanent constant lag
            // that Video's own out-of-sync check (which only ever compares against its own
            // PlaybackPosition, never the true clock) can never detect or correct. Measured against
            // several real cached maps, an uncorrected freeze left a lag anywhere from tens of ms
            // up to ~2.85s depending on how long the catch-up took — confirm PlaybackPosition
            // tracks the video's own live clock (Time.Current) to within a couple of frames, not
            // just that it advances at all.
            AddAssert("no residual lag baked in after catch-up",
                () =>
                {
                    var video = visuals.ChildrenOfType<osu.Framework.Graphics.Video.Video>().Single();
                    return Math.Abs(video.PlaybackPosition - video.Time.Current) < 100;
                });

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        // 1x1 red pixel PNG — content is irrelevant, only that it decodes to a valid texture.
        private static byte[] solidPng() => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    }
}
