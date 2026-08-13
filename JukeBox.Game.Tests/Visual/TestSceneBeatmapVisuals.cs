#nullable enable

using System;
using System.IO;
using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Playback;
using JukeBox.Game.Screens;
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

        // Background auto-hide follows lazer's Storyboard.ReplacesBackground: only a storyboard
        // that explicitly draws the beatmap background as one of its own Background-layer sprites
        // hides our separate background sprite. Requires the .osu's own background event so the
        // metadata knows which file the background IS.
        [Test]
        public void BackgroundHidesWhenStoryboardReplacesIt()
        {
            BeatmapVisuals visuals = null!;

            AddStep("create visuals with bg-replacing storyboard", () =>
            {
                string osuFile = Path.Combine(tmp, "replace [x].osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 0

                    [Events]
                    0,0,"bg.png",0,0
                    Sprite,Background,Centre,"bg.png",320,240
                    _F,0,0,5000,0,1
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 5,
                    Directory = tmp,
                    BackgroundFile = fixtureSetA.BackgroundFile,
                    OsuFiles = { osuFile },
                    PreferredOsuFile = osuFile,
                };

                Add(visuals = new BeatmapVisuals(set, playbackClock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("visuals loaded", () => visuals.IsLoaded);
            AddAssert("storyboard has objects", () => visuals.StoryboardLayer.HasObjects);
            AddAssert("storyboard replaces background", () => visuals.StoryboardLayer.ShouldHideBackground);
            AddAssert("background hidden", () => !visuals.BackgroundVisible);

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        // The counterpart rule: a storyboard whose sprites do NOT draw the background image keeps
        // our background visible underneath — mappers rely on it (the old blanket any-objects hide
        // was wrong).
        [Test]
        public void BackgroundStaysUnderNonReplacingStoryboard()
        {
            BeatmapVisuals visuals = null!;

            AddStep("create visuals with non-replacing storyboard", () =>
            {
                string osuFile = Path.Combine(tmp, "keepbg [x].osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 0

                    [Events]
                    0,0,"bg.png",0,0
                    Sprite,Foreground,Centre,"other.png",320,240
                    _F,0,0,5000,0,1
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 7,
                    Directory = tmp,
                    BackgroundFile = fixtureSetA.BackgroundFile,
                    OsuFiles = { osuFile },
                    PreferredOsuFile = osuFile,
                };

                Add(visuals = new BeatmapVisuals(set, playbackClock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("visuals loaded", () => visuals.IsLoaded);
            AddAssert("storyboard has objects", () => visuals.StoryboardLayer.HasObjects);
            AddAssert("background stays visible", () => visuals.BackgroundVisible);

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
        // corrupt/unsupported video file (now a storyboard Video event rendered by lazer's
        // DrawableStoryboardVideo) must not prevent BeatmapVisuals from loading, and once the
        // decoder faults (asynchronously, on its own thread) the background must come back
        // rather than leaving a permanently black screen.
        [Test]
        public void CorruptVideoFileDoesNotPreventLoad()
        {
            BeatmapVisuals visuals = null!;

            AddStep("create visuals with garbage video", () =>
            {
                File.WriteAllBytes(Path.Combine(tmp, "garbage.mp4"), new byte[] { 0x00, 0x01, 0x02, 0x03 });

                string osuFile = Path.Combine(tmp, "badvideo [x].osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 0

                    [Events]
                    0,0,"bg.png",0,0
                    Video,0,"garbage.mp4"
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 3,
                    Directory = tmp,
                    BackgroundFile = fixtureSetA.BackgroundFile,
                    OsuFiles = { osuFile },
                    PreferredOsuFile = osuFile,
                };

                Add(visuals = new BeatmapVisuals(set, playbackClock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("visuals loaded despite bad video", () => visuals.IsLoaded);
            AddAssert("storyboard carries the video event", () => visuals.StoryboardLayer.HasVideo);

            AddUntilStep("video counts as gone after decoder fault", () => !visuals.HasVideoLayer);
            AddAssert("visuals still alive (no crash from the bad video)", () => !visuals.Disposed);
            AddUntilStep("background restored after fault", () => visuals.BackgroundVisible);

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
            AddAssert("fell back to empty storyboard",
                () => visuals.StoryboardLayer.ElementCount == 0 && !visuals.StoryboardLayer.HasObjects);

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

        // Regression test for the mid-song video seek-storm, now exercising LAZER's video path
        // (DrawableStoryboardVideo seeds PlaybackPosition once in LoadComplete, then framework
        // Video's own per-frame sync takes over): a video-having set (re)built with the clock
        // already well past 0 — the normal case here, since radio songs start mid-track and diff
        // switches land mid-song — must actually land synced frames instead of chasing a moving
        // re-seek target forever. The fixture video has a single keyframe at t=0 covering its
        // whole ~8s (forced via a large libx264 GOP), so starting 6s in forces exactly the deep
        // decode-forward-through-the-GOP catch-up that triggered the storm in the old hand-rolled
        // path. This test is the gate for adopting lazer's video path wholesale.
        [Test]
        public void VideoCatchesUpWhenSeekedDeepIntoSongOnConstruction()
        {
            RealTimeClockPump pump = null!;
            BeatmapVisuals visuals = null!;

            AddStep("create real-time clock 6s into the song", () => Add(pump = new RealTimeClockPump(6000)));

            AddStep("create visuals with the single-keyframe fixture video", () =>
            {
                File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "sync-test-video.mp4"),
                    Path.Combine(tmp, "sync-test-video.mp4"), true);

                string osuFile = Path.Combine(tmp, "video [x].osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 0

                    [Events]
                    Video,0,"sync-test-video.mp4"
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 6,
                    Directory = tmp,
                    OsuFiles = { osuFile },
                    PreferredOsuFile = osuFile,
                };

                Add(visuals = new BeatmapVisuals(set, pump.Clock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("visuals loaded", () => visuals.IsLoaded);
            AddAssert("storyboard carries the video", () => visuals.StoryboardLayer.HasVideo);

            // A re-seek storm never produces a synced frame at all — this is the definitive
            // catches-up-or-storms signal.
            AddUntilStep("video catches up and starts rendering synced frames",
                () => (visuals.VideoFramesProcessed ?? 0) > 0);

            // Once rendering, playback position must track the clock within the framework's own
            // re-seek lenience (~2.5s) — beyond that it would be permanently re-seeking.
            AddAssert("playback position tracks the clock",
                () =>
                {
                    var video = visuals.ChildrenOfType<osu.Framework.Graphics.Video.Video>().Single();
                    return Math.Abs(video.PlaybackPosition - video.Time.Current) < 2500;
                });

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        // 1x1 red pixel PNG — content is irrelevant, only that it decodes to a valid texture.
        private static byte[] solidPng() => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    }
}
