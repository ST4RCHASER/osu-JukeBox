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

            // The garbage file doesn't fail Video's constructor synchronously — the decoder
            // faults asynchronously on its own thread a little later (confirmed empirically: the
            // runtime logs "VideoDecoder faulted: ... Invalid data found when processing input").
            // BeatmapVisuals.Update() polls Video.IsFaulted and tears the layer down once that
            // happens; assert that actually occurs rather than just that nothing crashed.
            AddUntilStep("video layer torn down after decoder fault", () => !visuals.HasVideoLayer);
            AddAssert("visuals still alive (no crash from the bad video)", () => !visuals.Disposed);

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
                () => visuals.ChildrenOfType<StoryboardLayer>().Single().VisibleSpriteCount == 0);

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        // 1x1 red pixel PNG — content is irrelevant, only that it decodes to a valid texture.
        private static byte[] solidPng() => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    }
}
