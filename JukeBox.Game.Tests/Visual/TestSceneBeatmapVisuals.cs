#nullable enable

using System;
using System.IO;
using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Playback;
using JukeBox.Game.Screens;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Screens;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Game.Rulesets.Catch;
using SixLabors.ImageSharp;
using osu.Game.Rulesets.Catch.UI;

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

        // Mirrors MainScreen's own [Cached] playerBoxSize (see BeatmapVisuals' resolved use of
        // it) — left at its default (0,0) for every other test in this fixture, which falls back
        // to BeatmapVisuals' own DrawSize exactly as before; only
        // TaikoLaneReachesTheLiveBoxEdgesWhenTheBoxIsWiderThan16By9 below assigns it.
        [Cached]
        private readonly osu.Framework.Bindables.Bindable<osuTK.Vector2> playerBoxSize = new();

        [Resolved]
        private JukeBoxConfigManager config { get; set; } = null!;

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

        // Regression coverage for the stale low-res background bug: a JukeBoxSetting.PlayfieldZoom
        // change (MainScreen zooms the whole scene, well outside this fixture, but the setting is
        // shared) must force backgroundBlurContainer's cached framebuffer to redraw — see
        // BeatmapVisuals.LoadComplete's own remarks on why RedrawOnScale=false alone isn't enough
        // across this setting's wide 1%-200% range. Covers the wiring itself (deterministic, no GPU
        // pixel readback needed); the framebuffer's actual resolution-at-redraw-time is
        // osu.Framework's own already-tested BufferedContainer machinery.
        [Test]
        public void PlayfieldZoomChangeForcesBackgroundRedraw()
        {
            BeatmapVisuals visuals = null!;

            AddStep("reset zoom to default", () => config.SetValue(JukeBoxSetting.PlayfieldZoom, 1.0));

            AddStep("create visuals with a background", () => Add(visuals = new BeatmapVisuals(fixtureSetA, playbackClock)
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddUntilStep("visuals loaded", () => visuals.IsLoaded);
            AddAssert("no forced redraw yet", () => visuals.BackgroundZoomForceRedrawCount == 0);

            AddStep("zoom out to 1%", () => config.SetValue(JukeBoxSetting.PlayfieldZoom, 0.01));
            AddAssert("background forced to redraw", () => visuals.BackgroundZoomForceRedrawCount == 1);

            AddStep("zoom back to 100%", () => config.SetValue(JukeBoxSetting.PlayfieldZoom, 1.0));
            AddAssert("background forced to redraw again on the way back", () => visuals.BackgroundZoomForceRedrawCount == 2);

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

        // The reported bug (set 683417): the .osu's [Events] references "…MV.avi" while the .osz
        // ships "…MV.mp4" — osu!'s re-encode keeps the ORIGINAL name in the map. Lazer's
        // DrawableStoryboardVideo asks the store for the referenced path and, on a null stream,
        // adds no child at all — so nothing loads AND nothing faults, VideoFaulted stayed false,
        // the background was hidden for a video that could never draw, and the result was black.
        //
        // The store now resolves across that mismatch, so the real outcome is that the video PLAYS.
        [Test]
        public void AVideoReferencedByTheWrongExtensionStillPlays()
        {
            BeatmapVisuals visuals = null!;

            AddStep("create visuals whose video is shipped as .mp4 but referenced as .avi", () =>
            {
                // A real, decodable clip under the name the set actually ships.
                File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "sync-test-video.mp4"),
                    Path.Combine(tmp, "reencoded.mp4"), true);

                string osuFile = Path.Combine(tmp, "extmismatch [x].osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 0

                    [Events]
                    0,0,"bg.png",0,0
                    Video,0,"reencoded.avi"
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 31,
                    Directory = tmp,
                    BackgroundFile = fixtureSetA.BackgroundFile,
                    OsuFiles = { osuFile },
                    PreferredOsuFile = osuFile,
                };

                Add(visuals = new BeatmapVisuals(set, playbackClock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("visuals loaded", () => visuals.IsLoaded);
            AddAssert("the storyboard carries the video event", () => visuals.StoryboardLayer.HasVideo);

            AddUntilStep("the video resolved despite the extension mismatch", () => visuals.HasVideoLayer);
            AddAssert("so it is not reported missing", () => !visuals.StoryboardLayer.VideoMissing);

            // And with a video that genuinely plays, the background hides as it always did.
            AddUntilStep("background hidden behind the playing video", () => !visuals.BackgroundVisible);

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        // The backstop for whatever the resolution fix above cannot rescue — a video that is simply
        // not in the folder under any extension. It must never leave a black screen: no Video
        // drawable is created at all in that case (so it never "faults"), which is exactly the hole
        // that made 683417 black.
        [Test]
        public void AnAbsentVideoFileLeavesTheBackgroundVisibleRatherThanBlack()
        {
            BeatmapVisuals visuals = null!;

            AddStep("create visuals whose video file does not exist", () =>
            {
                string osuFile = Path.Combine(tmp, "novideofile [x].osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 0

                    [Events]
                    0,0,"bg.png",0,0
                    Video,0,"nothing-here.avi"
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 32,
                    Directory = tmp,
                    BackgroundFile = fixtureSetA.BackgroundFile,
                    OsuFiles = { osuFile },
                    PreferredOsuFile = osuFile,
                };

                Add(visuals = new BeatmapVisuals(set, playbackClock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("visuals loaded", () => visuals.IsLoaded);
            AddAssert("the storyboard still carries the video event", () => visuals.StoryboardLayer.HasVideo);

            AddUntilStep("the missing file is recognised as such", () => visuals.StoryboardLayer.VideoMissing);
            AddAssert("so it does not count as a playable video", () => !visuals.HasVideoLayer);
            AddAssert("and the background is not hidden for it", () => !visuals.StoryboardLayer.ShouldHideBackground);
            AddUntilStep("the user sees the background, not black", () => visuals.BackgroundVisible);

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

        // The app Volume setting (PlaybackController.Volume, driven by JukeBoxSetting.Volume) must
        // reach lazer-rendered audio (storyboard Sample events / keysounds, chart hitsounds), not
        // just the music track — see BeatmapVisuals.audioAdjustments remarks. AggregateVolume is
        // what actually reaches every DrawableAudioWrapper-based sample under it (skin lookups
        // included), so asserting on that — not just the local Volume bindable — is the real check
        // that the binding cascades through the drawable hierarchy the way lazer's own gameplay
        // volume does.
        [Test]
        public void AudioAdjustmentsFollowAppVolume()
        {
            BeatmapVisuals visuals = null!;

            AddStep("create visuals", () => Add(visuals = new BeatmapVisuals(fixtureSetA, playbackClock)
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddUntilStep("visuals loaded", () => visuals.IsLoaded);
            AddAssert("aggregate volume starts at full app volume", () => visuals.AudioAdjustments.AggregateVolume.Value == controller.Volume.Value);

            AddStep("lower app volume", () => controller.Volume.Value = 0.25);
            AddAssert("aggregate volume follows app volume down", () => visuals.AudioAdjustments.AggregateVolume.Value == 0.25);

            AddStep("mute app volume", () => controller.Volume.Value = 0);
            AddAssert("aggregate volume follows app volume to silence", () => visuals.AudioAdjustments.AggregateVolume.Value == 0);

            AddStep("restore app volume", () => controller.Volume.Value = 1);
            AddAssert("aggregate volume follows app volume back up", () => visuals.AudioAdjustments.AggregateVolume.Value == 1);

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        // Regression coverage for the catch (CTB) crop bug and its follow-up edge-case bugs:
        // unlike standard/taiko/mania, whose lazer PlayfieldAdjustmentContainers size themselves
        // in RELATIVE fractions of whatever box they're given (scale-invariant), catch's
        // CatchPlayfieldAdjustmentContainer uses ABSOLUTE pixel constants (a 1024×768 "base game"
        // canvas) calibrated against lazer's own Player-screen convention
        // (DrawSizePreservingFillContainer.TargetSize) — chartContainer must actually BE a
        // 1024×768 canvas (see its construction comment in BeatmapVisuals) or catch renders ~2x
        // oversized and the catcher/fruits end up entirely outside the box.
        //
        // An earlier version of this test asserted full containment within `visuals`' own bounds,
        // which required forcibly shrinking catch's playfield (dividing by 968 = lazer's
        // base_game_height + its own internal safety margin) to fit flush with zero space to
        // spare — that flush fit turned catch's own internal (normally harmless/oversized) safety
        /// <summary>
        /// A Chart-tab mod change has to REBUILD the chart layer, not just sit in the selection:
        /// mods change the beatmap conversion and the autoplay replay walked over it, both of which
        /// are built once per layer. Same rebuild-on-revision mechanism a skin change already uses.
        /// </summary>
        [Test]
        public void ChangingChartModsRebuildsTheChartLayerWithThem()
        {
            BeatmapVisuals visuals = null!;
            LazerChartLayer firstBuild = null!;

            AddStep("enable chart, no mods", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, true);
                config.SetValue(JukeBoxSetting.ChartMods, string.Empty);
            });

            AddStep("create visuals with an osu! difficulty", () =>
            {
                string osuFile = Path.Combine(tmp, "mods [x].osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 0

                    [Difficulty]
                    HPDrainRate:5
                    CircleSize:4
                    OverallDifficulty:5
                    ApproachRate:5
                    SliderMultiplier:1.4
                    SliderTickRate:1

                    [TimingPoints]
                    0,500,4,1,0,100,1,0

                    [HitObjects]
                    64,192,1000,1,0
                    192,192,1500,1,0
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 11,
                    Directory = tmp,
                    BackgroundFile = fixtureSetA.BackgroundFile,
                    OsuFiles = { osuFile },
                    PreferredOsuFile = osuFile,
                };

                Add(visuals = new BeatmapVisuals(set, playbackClock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("chart loaded", () => visuals.IsLoaded && visuals.HasChartLayer && visuals.ChartRenderer?.DrawableRuleset != null);
            AddAssert("built unmodded", () => visuals.ChartRenderer!.DrawableRuleset!.Mods.All(m => m.Acronym != "HR"));
            AddStep("remember the layer instance", () => firstBuild = visuals.ChartRenderer!);

            AddStep("select HR in the Chart tab", () => config.SetValue(JukeBoxSetting.ChartMods, "HR"));

            AddUntilStep("the chart was rebuilt", () => visuals.ChartRenderer != null
                                                       && !ReferenceEquals(visuals.ChartRenderer, firstBuild)
                                                       && visuals.ChartRenderer.DrawableRuleset != null);

            AddAssert("and the rebuild carries the mod", () => visuals.ChartRenderer!.DrawableRuleset!.Mods.Any(m => m.Acronym == "HR"));

            AddStep("restore settings", () =>
            {
                config.SetValue(JukeBoxSetting.ChartMods, string.Empty);
                config.SetValue(JukeBoxSetting.RenderChart, false);
                Remove(visuals, true);
            });
        }

        /// <summary>
        /// Changing "Convert to" has to REBUILD the chart layer, not just sit in the service: the
        /// conversion decides which ruleset the beatmap is built for, which is settled once per
        /// layer. Same rebuild-on-revision mechanism the mods and skin changes already use.
        /// </summary>
        [Test]
        public void ChangingTheConversionTargetRebuildsTheChartLayerAsThatRuleset()
        {
            BeatmapVisuals visuals = null!;
            LazerChartLayer firstBuild = null!;

            AddStep("enable chart, no conversion", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, true);
                config.SetValue(JukeBoxSetting.ChartMods, string.Empty);
                config.SetValue(JukeBoxSetting.ConvertToRuleset, ChartConversionTarget.Off);
            });

            AddStep("create visuals with an osu! difficulty", () =>
            {
                string osuFile = Path.Combine(tmp, "convert [x].osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 0

                    [Difficulty]
                    HPDrainRate:5
                    CircleSize:4
                    OverallDifficulty:5
                    ApproachRate:5
                    SliderMultiplier:1.4
                    SliderTickRate:1

                    [TimingPoints]
                    0,500,4,1,0,100,1,0

                    [HitObjects]
                    64,192,1000,1,0
                    192,192,1500,1,0
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 12,
                    Directory = tmp,
                    BackgroundFile = fixtureSetA.BackgroundFile,
                    OsuFiles = { osuFile },
                    PreferredOsuFile = osuFile,
                };

                Add(visuals = new BeatmapVisuals(set, playbackClock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("chart loaded", () => visuals.IsLoaded && visuals.HasChartLayer && visuals.ChartRenderer?.DrawableRuleset != null);
            AddAssert("built as osu!", () => visuals.ChartRenderer!.Ruleset?.GetType() == typeof(osu.Game.Rulesets.Osu.OsuRuleset));
            AddStep("remember the layer instance", () => firstBuild = visuals.ChartRenderer!);

            AddStep("convert to taiko", () => config.SetValue(JukeBoxSetting.ConvertToRuleset, ChartConversionTarget.Taiko));

            AddUntilStep("the chart was rebuilt", () => visuals.ChartRenderer != null
                                                       && !ReferenceEquals(visuals.ChartRenderer, firstBuild)
                                                       && visuals.ChartRenderer.DrawableRuleset != null);

            AddAssert("and the rebuild is a taiko chart",
                () => visuals.ChartRenderer!.Ruleset?.GetType() == typeof(osu.Game.Rulesets.Taiko.TaikoRuleset));

            AddStep("restore settings", () =>
            {
                config.SetValue(JukeBoxSetting.ConvertToRuleset, ChartConversionTarget.Off);
                config.SetValue(JukeBoxSetting.RenderChart, false);
                Remove(visuals, true);
            });
        }

        // clip into a hard, VISIBLE seam exactly at the scene edge, clipping fruits mid-sprite as
        // they entered from the top, and left a large dead gap below the catcher. See
        // catch_reserved_height's remarks in BeatmapVisuals for the corrected approach: catch is
        // allowed to overflow `visuals`' own bounds by design (unclipped — nothing between
        // chartContainer and MainScreen's playerBox masks), so the invariants checked here are
        // narrower and more precise than "fits inside the box".
        [Test]
        public void CatchCatcherSitsNearBottomWithNoIntermediateMasking()
        {
            BeatmapVisuals visuals = null!;

            // A locally-advancing clock (unlike the shared, frozen playbackClock field) — a real
            // FALLING fruit is needed to check the top-clip regression, and a frozen clock never
            // spawns one.
            var manual = new ManualClock();
            var clock = new FramedClock(manual);

            AddStep("enable chart", () => config.SetValue(JukeBoxSetting.RenderChart, true));

            AddStep("create visuals with a catch difficulty", () =>
            {
                string osuFile = Path.Combine(tmp, "catch [x].osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 2

                    [Difficulty]
                    HPDrainRate:5
                    CircleSize:4
                    OverallDifficulty:5
                    ApproachRate:5
                    SliderMultiplier:1.4
                    SliderTickRate:1

                    [TimingPoints]
                    0,500,4,1,0,100,1,0

                    [HitObjects]
                    64,192,1000,1,0
                    192,192,1500,1,0
                    256,192,2000,1,0
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 9,
                    Directory = tmp,
                    BackgroundFile = fixtureSetA.BackgroundFile,
                    OsuFiles = { osuFile },
                    PreferredOsuFile = osuFile,
                };

                Add(visuals = new BeatmapVisuals(set, clock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("chart loaded", () => visuals.IsLoaded && visuals.HasChartLayer && visuals.ChartRenderer?.DrawableRuleset != null);
            AddAssert("catch ruleset chosen", () => visuals.ChartRenderer!.Ruleset?.GetType() == typeof(CatchRuleset));

            // Approach rate 5 preempts ~1200ms before the first object's 1000ms hit time (spawns
            // around -200ms) — 500ms lands mid-fall, well clear of both spawn and hit.
            AddStep("advance to mid-fall", () =>
            {
                manual.CurrentTime = 500;
                clock.ProcessFrame();
            });
            AddUntilStep("a fruit is falling", () => visuals.ChartRenderer!.DrawableRuleset!.Playfield.AllHitObjects.Any());

            // Full containment within `visuals`' own bounds is deliberately NOT asserted here —
            // catch's playfield legitimately overflows both edges of that nominal box by design
            // (see catch_reserved_height's remarks in BeatmapVisuals), and is meant to render
            // unclipped into whatever letterbox margin MainScreen's playerBox provides around it.
            // The two invariants that DO hold at this level:
            AddAssert("catcher sits near the bottom of the scene (~90-97% down, real-lazer proportions)", () =>
            {
                var box = visuals.ScreenSpaceDrawQuad;
                var catcher = visuals.ChartRenderer!.DrawableRuleset!.ChildrenOfType<Catcher>().Single();
                var catcherQuad = catcher.ScreenSpaceDrawQuad;

                // Catcher's own Origin is TopCentre (lazer's Catcher.cs), so its top edge is the
                // meaningful single Y reference — matches how it's actually anchored/positioned.
                float boxHeight = box.BottomLeft.Y - box.TopLeft.Y;
                float catcherTopFraction = (catcherQuad.TopLeft.Y - box.TopLeft.Y) / boxHeight;

                return catcherTopFraction is >= 0.87f and <= 0.98f;
            });
            // Lazer's own CatchPlayfieldAdjustmentContainer has an internal "Visible area" node
            // with Masking = true — that's inherent to catch's real ruleset (a generous safety
            // clip against extreme aspect ratios) and genuinely sits in the ancestor chain, so
            // asserting its mere presence away isn't meaningful (it's also, by design, WIDER than
            // Playfield's own structural bounds on at least the top edge — that's slack in
            // Playfield's own layout, not visible content, so it's not what's checked here). What
            // actually matters (and is what broke before this fix — see the flush-968-fit note
            // above): the actual falling FRUIT sprite the user sees must not be cut by any masking
            // ancestor. If some ancestor's own bounds don't fully contain the fruit that's
            // currently on screen, that's a real visible-clip regression.
            AddAssert("no masking ancestor between the visuals box and a real falling fruit actually clips it", () =>
            {
                var ruleset = visuals.ChartRenderer!.DrawableRuleset!;
                var fruit = ruleset.Playfield.AllHitObjects.First();
                var fruitQuad = fruit.ScreenSpaceDrawQuad;

                Drawable? d = fruit;

                while (d != null && d != visuals)
                {
                    if (d is CompositeDrawable { Masking: true } comp)
                    {
                        var maskQuad = comp.ScreenSpaceDrawQuad;

                        bool containsFruit = fruitQuad.TopLeft.Y >= maskQuad.TopLeft.Y - 0.5f
                                             && fruitQuad.BottomLeft.Y <= maskQuad.BottomLeft.Y + 0.5f;

                        if (!containsFruit)
                            return false;
                    }

                    d = d.Parent;
                }

                return true;
            });

            AddStep("restore settings", () => config.SetValue(JukeBoxSetting.RenderChart, false));
            AddStep("remove visuals", () => Remove(visuals, true));
        }

        // Regression coverage for taiko lane alignment: unlike catch's PlayfieldAdjustmentContainer
        // (absolute-pixel math, needed the mode-aware 1024x768 hosting fix above),
        // TaikoPlayfieldAdjustmentContainer sizes itself in RELATIVE, scale-invariant fractions of
        // whatever box it's given, clamped to an aspect range of [5:4, 16:9]
        // (TaikoPlayfieldAdjustmentContainer.MINIMUM_ASPECT/MAXIMUM_ASPECT) — chartContainer's
        // fixed 1024x768 (4:3, aspect 1.333) sits comfortably inside that range, so no clamping
        // (and therefore no distortion) applies. The hit target ("Elements behind hit objects")
        // and every DrawableHit both live in the same RelativeSizeAxes.Both "Right area" row of
        // TaikoPlayfield, so their vertical centres are expected to coincide exactly at all times
        // up to and including the hit — this asserts that invariant under our real chartContainer
        // hosting geometry, for both the default (Argon) skin and a legacy (Classic) skin, the
        // two configurations a beatmap's own "Beatmap skins" toggle can produce.
        [TestCase(JukeBoxSkin.Argon)]
        [TestCase(JukeBoxSkin.Classic)]
        public void TaikoNoteAlignsWithHitTargetBeforeHit(JukeBoxSkin skin)
        {
            BeatmapVisuals visuals = null!;
            var manual = new osu.Framework.Timing.ManualClock();
            osu.Framework.Timing.FramedClock? clock = null;

            AddStep("enable chart + select skin", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, true);
                config.SetValue(JukeBoxSetting.Skin, skin);
            });

            AddStep("create visuals with a taiko difficulty", () =>
            {
                string osuFile = Path.Combine(tmp, $"taiko [{skin}].osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 1

                    [Difficulty]
                    HPDrainRate:5
                    CircleSize:4
                    OverallDifficulty:5
                    ApproachRate:5
                    SliderMultiplier:1.4
                    SliderTickRate:1

                    [TimingPoints]
                    0,500,4,1,0,100,1,0

                    [HitObjects]
                    64,192,5000,1,0
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 11,
                    Directory = tmp,
                    BackgroundFile = fixtureSetA.BackgroundFile,
                    OsuFiles = { osuFile },
                    PreferredOsuFile = osuFile,
                };

                clock = new osu.Framework.Timing.FramedClock(manual);
                Add(visuals = new BeatmapVisuals(set, clock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("chart loaded", () => visuals.IsLoaded && visuals.HasChartLayer && visuals.ChartRenderer?.DrawableRuleset != null);
            AddAssert("taiko ruleset chosen", () => visuals.ChartRenderer!.Ruleset?.GetType() == typeof(osu.Game.Rulesets.Taiko.TaikoRuleset));

            // Sample well before the hit (still scrolling in) and right at the hit instant —
            // deliberately BEFORE DrawableHit's post-judgement "gravity" fly-away transform
            // (this.MoveToY(-200, ...) in DrawableHit.UpdateHitStateTransforms) has moved it, since
            // that transform is an intentional lazer/stable visual flourish, not a layout bug.
            foreach (double time in new[] { 4500d, 4990d, 5000d })
            {
                AddStep($"advance to {time}ms", () =>
                {
                    manual.CurrentTime = time;
                    clock!.ProcessFrame();
                });

                AddUntilStep($"note visible @ {time}ms", () =>
                    visuals.ChartRenderer!.DrawableRuleset!.Playfield.AllHitObjects
                           .OfType<osu.Game.Rulesets.Taiko.Objects.Drawables.DrawableHit>().Any());

                AddAssert($"note centre Y matches hit target centre Y @ {time}ms", () =>
                {
                    var playfield = visuals.ChartRenderer!.DrawableRuleset!.Playfield;
                    var target = playfield.ChildrenOfType<osu.Framework.Graphics.Containers.Container>()
                                           .Single(c => c.Name == "Elements behind hit objects");
                    var note = playfield.AllHitObjects.OfType<osu.Game.Rulesets.Taiko.Objects.Drawables.DrawableHit>().Single();

                    float targetCentre = (target.ScreenSpaceDrawQuad.TopLeft.Y + target.ScreenSpaceDrawQuad.BottomLeft.Y) / 2;
                    float noteCentre = (note.ScreenSpaceDrawQuad.TopLeft.Y + note.ScreenSpaceDrawQuad.BottomLeft.Y) / 2;

                    return Math.Abs(noteCentre - targetCentre) < 2f;
                });
            }

            AddStep("remove visuals", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, false);
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Argon);
                Remove(visuals, true);
            });
        }

        // Regression coverage for the reopened report: a beatmap shipping its OWN partial legacy
        // taiko skin (provides "taikohitcircle"/"taikohitcircleoverlay" for notes — a common
        // "kirby-style" skin authoring pattern — but NOT "taikobigcircle" for the hit target, so
        // the target must fall back through the chain) combined with a NON-legacy user skin
        // (Triangles) or the legacy Classic selection, Beatmap skins on — the exact path through
        // BeatmapFolderSkin → BeatmapSkinGate → LazerSkinProvider the prior (no-beatmap-skin) test
        // didn't exercise. Confirms both that the beatmap's own texture actually resolves (so the
        // fallback chain is genuinely exercised, not accidentally skipped) and that the note stays
        // aligned with the target.
        [TestCase(JukeBoxSkin.Triangles)]
        [TestCase(JukeBoxSkin.Classic)]
        public void TaikoBeatmapSkinNoteAlignsWithHitTargetBeforeHit(JukeBoxSkin skin)
        {
            BeatmapVisuals visuals = null!;
            var manual = new osu.Framework.Timing.ManualClock();
            osu.Framework.Timing.FramedClock? clock = null;

            AddStep("enable chart + select skin", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, true);
                config.SetValue(JukeBoxSetting.Skin, skin);
            });

            AddStep("create visuals with a taiko difficulty + beatmap-provided partial legacy skin", () =>
            {
                string mapDir = Path.Combine(tmp, $"beatmapskin-{skin}");
                Directory.CreateDirectory(mapDir);

                // Beatmap provides its OWN note textures but deliberately omits "taikobigcircle" —
                // the hit target must fall back to the user/Classic chain.
                File.WriteAllBytes(Path.Combine(mapDir, "taikohitcircle.png"), solidPng());
                File.WriteAllBytes(Path.Combine(mapDir, "taikohitcircleoverlay.png"), solidPng());
                File.WriteAllBytes(Path.Combine(mapDir, "bg.png"), solidPng());

                string osuFile = Path.Combine(mapDir, "taiko [x].osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 1

                    [Difficulty]
                    HPDrainRate:5
                    CircleSize:4
                    OverallDifficulty:5
                    ApproachRate:5
                    SliderMultiplier:1.4
                    SliderTickRate:1

                    [TimingPoints]
                    0,500,4,1,0,100,1,0

                    [HitObjects]
                    64,192,5000,1,0
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 12,
                    Directory = mapDir,
                    BackgroundFile = Path.Combine(mapDir, "bg.png"),
                    OsuFiles = { osuFile },
                    PreferredOsuFile = osuFile,
                };

                clock = new osu.Framework.Timing.FramedClock(manual);
                Add(visuals = new BeatmapVisuals(set, clock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("chart loaded", () => visuals.IsLoaded && visuals.HasChartLayer && visuals.ChartRenderer?.DrawableRuleset != null);

            // Sample well before the hit and right at the hit instant — before DrawableHit's
            // intentional post-judgement "gravity" fly-away transform (see
            // TaikoNoteAlignsWithHitTargetBeforeHit's remarks) could move it.
            foreach (double time in new[] { 4500d, 4990d, 5000d })
            {
                AddStep($"advance to {time}ms", () =>
                {
                    manual.CurrentTime = time;
                    clock!.ProcessFrame();
                });

                AddUntilStep($"note visible @ {time}ms", () =>
                    visuals.ChartRenderer!.DrawableRuleset!.Playfield.AllHitObjects
                           .OfType<osu.Game.Rulesets.Taiko.Objects.Drawables.DrawableHit>().Any());

                AddAssert($"note resolves via the beatmap's own legacy texture @ {time}ms", () =>
                {
                    var note = visuals.ChartRenderer!.DrawableRuleset!.Playfield.AllHitObjects
                                       .OfType<osu.Game.Rulesets.Taiko.Objects.Drawables.DrawableHit>().Single();
                    return note.ChildrenOfType<osu.Game.Rulesets.Taiko.Skinning.Legacy.LegacyCirclePiece>().Any();
                });

                AddAssert($"note centre Y matches hit target centre Y @ {time}ms", () =>
                {
                    var playfield = visuals.ChartRenderer!.DrawableRuleset!.Playfield;
                    var target = playfield.ChildrenOfType<osu.Framework.Graphics.Containers.Container>()
                                           .Single(c => c.Name == "Elements behind hit objects");
                    var note = playfield.AllHitObjects.OfType<osu.Game.Rulesets.Taiko.Objects.Drawables.DrawableHit>().Single();

                    float targetCentre = (target.ScreenSpaceDrawQuad.TopLeft.Y + target.ScreenSpaceDrawQuad.BottomLeft.Y) / 2;
                    float noteCentre = (note.ScreenSpaceDrawQuad.TopLeft.Y + note.ScreenSpaceDrawQuad.BottomLeft.Y) / 2;

                    return Math.Abs(noteCentre - targetCentre) < 2f;
                });
            }

            AddStep("remove visuals", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, false);
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Argon);
                Remove(visuals, true);
            });
        }

        // Regression coverage for reopen #4: a compositional mismatch distinct from every prior
        // check in this investigation. Those all compared the NOTE's position against the HIT
        // TARGET's — both live in TaikoPlayfield's own "Right area" and were always going to
        // agree. This instead checks the LANE BACKGROUND (TaikoSkinComponents.
        // PlayfieldBackgroundRight — despite the "Right" name, it's added as an unpadded,
        // RelativeSizeAxes.Both TOP-LEVEL child of TaikoPlayfield, i.e. the single continuous
        // backdrop spanning both the drum and the note areas, matching stable's one-piece dark
        // lane) against the playfield's own bounds, the drum area ("Left overlay"), and the note
        // — the exact three pieces reported detached from each other. A beatmap-provided legacy
        // taiko skin (its own "taiko-bar-right"/"taikohitcircle" — the same "kirby-style" pattern
        // as the prior beatmap-skin investigation) is used with a non-legacy user skin (Triangles)
        // and the legacy Classic selection, Beatmap skins on; a bundled-Classic-only (no beatmap
        // skin) case is included as a control.
        [TestCase(JukeBoxSkin.Triangles, true)]
        [TestCase(JukeBoxSkin.Classic, true)]
        [TestCase(JukeBoxSkin.Classic, false)]
        public void TaikoLaneBackgroundContainsDrumAndNoteBeforeHit(JukeBoxSkin skin, bool beatmapProvidesOwnSkin)
        {
            BeatmapVisuals visuals = null!;
            var manual = new osu.Framework.Timing.ManualClock();
            osu.Framework.Timing.FramedClock? clock = null;

            AddStep("enable chart + select skin", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, true);
                config.SetValue(JukeBoxSetting.Skin, skin);
            });

            AddStep("create visuals", () =>
            {
                string mapDir = Path.Combine(tmp, $"lanebg-{skin}-{beatmapProvidesOwnSkin}");
                Directory.CreateDirectory(mapDir);

                if (beatmapProvidesOwnSkin)
                {
                    File.WriteAllBytes(Path.Combine(mapDir, "taiko-bar-right.png"), solidPng());
                    File.WriteAllBytes(Path.Combine(mapDir, "taikohitcircle.png"), solidPng());
                    File.WriteAllBytes(Path.Combine(mapDir, "taikohitcircleoverlay.png"), solidPng());
                }

                File.WriteAllBytes(Path.Combine(mapDir, "bg.png"), solidPng());

                string osuFile = Path.Combine(mapDir, "taiko [x].osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 1

                    [Difficulty]
                    HPDrainRate:5
                    CircleSize:4
                    OverallDifficulty:5
                    ApproachRate:5
                    SliderMultiplier:1.4
                    SliderTickRate:1

                    [TimingPoints]
                    0,500,4,1,0,100,1,0

                    [HitObjects]
                    64,192,5000,1,0
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 14,
                    Directory = mapDir,
                    BackgroundFile = Path.Combine(mapDir, "bg.png"),
                    OsuFiles = { osuFile },
                    PreferredOsuFile = osuFile,
                };

                clock = new osu.Framework.Timing.FramedClock(manual);
                Add(visuals = new BeatmapVisuals(set, clock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("chart loaded", () => visuals.IsLoaded && visuals.HasChartLayer && visuals.ChartRenderer?.DrawableRuleset != null);

            AddStep("advance to just before the hit", () =>
            {
                manual.CurrentTime = 4990;
                clock!.ProcessFrame();
            });

            AddUntilStep("note visible", () =>
                visuals.ChartRenderer!.DrawableRuleset!.Playfield.AllHitObjects
                       .OfType<osu.Game.Rulesets.Taiko.Objects.Drawables.DrawableHit>().Any());

            AddAssert("lane background resolved (legacy or default) and spans ~full playfield width", () =>
            {
                var playfield = visuals.ChartRenderer!.DrawableRuleset!.Playfield;
                var laneBg = laneBackground(playfield);
                if (laneBg == null)
                    return false;

                var playfieldQuad = playfield.ScreenSpaceDrawQuad;
                float playfieldWidth = playfieldQuad.TopRight.X - playfieldQuad.TopLeft.X;
                var laneQuad = laneBg.ScreenSpaceDrawQuad;
                float laneWidth = laneQuad.TopRight.X - laneQuad.TopLeft.X;

                return laneWidth >= playfieldWidth * 0.95f;
            });

            AddAssert("lane background contains the drum area", () =>
            {
                var playfield = visuals.ChartRenderer!.DrawableRuleset!.Playfield;
                var laneBg = laneBackground(playfield)!;
                var drum = playfield.ChildrenOfType<osu.Framework.Graphics.Containers.Container>()
                                     .Single(c => c.Name == "Left overlay");

                return quadContains(laneBg.ScreenSpaceDrawQuad, drum.ScreenSpaceDrawQuad);
            });

            AddAssert("lane background contains the note", () =>
            {
                var playfield = visuals.ChartRenderer!.DrawableRuleset!.Playfield;
                var laneBg = laneBackground(playfield)!;
                var note = playfield.AllHitObjects.OfType<osu.Game.Rulesets.Taiko.Objects.Drawables.DrawableHit>().Single();

                return quadContains(laneBg.ScreenSpaceDrawQuad, note.ScreenSpaceDrawQuad);
            });

            AddStep("remove visuals", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, false);
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Argon);
                Remove(visuals, true);
            });
        }

        private static Drawable? laneBackground(osu.Game.Rulesets.UI.Playfield playfield)
        {
            Drawable? legacy = playfield.ChildrenOfType<osu.Game.Rulesets.Taiko.Skinning.Legacy.TaikoLegacyPlayfieldBackgroundRight>().FirstOrDefault();
            if (legacy != null)
                return legacy;

            // Argon (JukeBox's default skin) resolves its own class for this component — never
            // falls to the default/legacy ones, unlike Triangles.
            Drawable? argon = playfield.ChildrenOfType<osu.Game.Rulesets.Taiko.Skinning.Argon.ArgonPlayfieldBackgroundRight>().FirstOrDefault();
            if (argon != null)
                return argon;

            return playfield.ChildrenOfType<osu.Game.Rulesets.Taiko.UI.PlayfieldBackgroundRight>().FirstOrDefault();
        }

        // Whether `inner` lies entirely within `outer`'s screen-space bounding box (axis-aligned,
        // small tolerance for floating-point noise).
        private static bool quadContains(osu.Framework.Graphics.Primitives.Quad outer, osu.Framework.Graphics.Primitives.Quad inner)
        {
            const float tolerance = 1f;
            return inner.TopLeft.X >= outer.TopLeft.X - tolerance && inner.TopRight.X <= outer.TopRight.X + tolerance
                   && inner.TopLeft.Y >= outer.TopLeft.Y - tolerance && inner.BottomLeft.Y <= outer.BottomLeft.Y + tolerance;
        }

        // Regression coverage for reopen #5, this time against the user's ACTUAL screenshots (not
        // a description): comparing jukebox-taiko.png/jukebox-taiko-2.png to real-game-taiko.png,
        // the banner, mascot and dark lane background all match closely, but the notes render
        // with ARGON's chevron-circle style (not the beatmap's own plain legacy circles) and the
        // input drum shows no coloured background box at all (Argon's minimal style) where the
        // real game shows a solid pink/cream legacy drum. This is a beatmap that provides only a
        // PARTIAL legacy taiko skin (e.g. just its lane background/banner plus — very commonly for
        // any "custom skin" beatmap — a combo-number font) but not "taikohitcircle"/"taiko-bar-left"
        // specifically. Confirmed the mechanism: TaikoArgonSkinTransformer unconditionally
        // overrides every taiko component (CentreHit, RimHit, InputDrum, ...) — unlike Triangles,
        // which barely touches taiko — so once the beatmap's own lookup misses, JukeBox's ORIGINAL
        // fallback chain (beatmap → user's selected skin → Classic, in that priority order) landed
        // on Argon's own completely different style for whatever the beatmap didn't cover, instead
        // of a consistent legacy look. Real lazer's BeatmapSkinProvidingContainer avoids exactly
        // this by inserting the classic skin at the SAME priority as the beatmap itself (checked
        // immediately after it, before ever reaching the user's actual non-legacy selection)
        // whenever the beatmap "is providing legacy resources" (LegacySkinTransformer.
        // IsProvidingLegacyResources) and the user's own skin isn't already legacy — the
        // "structural priority difference" flagged (and prematurely dismissed against Triangles,
        // which doesn't trigger it) in an earlier round of this investigation. LazerChartLayer now
        // replicates that same-priority insertion.
        [Test]
        public void TaikoPartialLegacyBeatmapSkinFallsBackToClassicNotArgon()
        {
            BeatmapVisuals visuals = null!;
            var manual = new osu.Framework.Timing.ManualClock();
            osu.Framework.Timing.FramedClock? clock = null;

            AddStep("enable chart, Argon skin (JukeBox's default)", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, true);
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Argon);
            });

            AddStep("create visuals with a partial-legacy beatmap skin", () =>
            {
                string mapDir = Path.Combine(tmp, "partial-legacy-skin");
                Directory.CreateDirectory(mapDir);

                // Only the lane background and a combo-number font — no "taikohitcircle", no
                // "taiko-bar-left" — the "decorative flair only" authoring pattern from the
                // report. "score-0.png" is what LegacySkinTransformer.IsProvidingLegacyResources
                // actually checks for by default (HasFont(LegacyFont.Combo) — ISkinExtensions.
                // GetFontPrefix defaults the combo font prefix to "score", not "default") and is
                // present on essentially every real "custom skin" beatmap, taiko or not.
                File.WriteAllBytes(Path.Combine(mapDir, "taiko-bar-right.png"), solidPng());
                File.WriteAllBytes(Path.Combine(mapDir, "score-0.png"), solidPng());
                File.WriteAllBytes(Path.Combine(mapDir, "bg.png"), solidPng());

                string osuFile = Path.Combine(mapDir, "taiko [x].osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 1

                    [Difficulty]
                    HPDrainRate:5
                    CircleSize:4
                    OverallDifficulty:5
                    ApproachRate:5
                    SliderMultiplier:1.4
                    SliderTickRate:1

                    [TimingPoints]
                    0,500,4,1,0,100,1,0

                    [HitObjects]
                    64,192,5000,1,0
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 15,
                    Directory = mapDir,
                    BackgroundFile = Path.Combine(mapDir, "bg.png"),
                    OsuFiles = { osuFile },
                    PreferredOsuFile = osuFile,
                };

                clock = new osu.Framework.Timing.FramedClock(manual);
                Add(visuals = new BeatmapVisuals(set, clock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("chart loaded", () => visuals.IsLoaded && visuals.HasChartLayer && visuals.ChartRenderer?.DrawableRuleset != null);

            AddStep("advance to just before the hit", () =>
            {
                manual.CurrentTime = 4990;
                clock!.ProcessFrame();
            });

            AddUntilStep("note visible", () =>
                visuals.ChartRenderer!.DrawableRuleset!.Playfield.AllHitObjects
                       .OfType<osu.Game.Rulesets.Taiko.Objects.Drawables.DrawableHit>().Any());

            AddAssert("note piece is the legacy circle, not Argon's", () =>
            {
                var note = visuals.ChartRenderer!.DrawableRuleset!.Playfield.AllHitObjects
                                   .OfType<osu.Game.Rulesets.Taiko.Objects.Drawables.DrawableHit>().Single();

                bool hasLegacyPiece = note.ChildrenOfType<osu.Game.Rulesets.Taiko.Skinning.Legacy.LegacyCirclePiece>().Any();
                bool hasArgonPiece = note.ChildrenOfType<osu.Game.Rulesets.Taiko.Skinning.Argon.ArgonCirclePiece>().Any();

                return hasLegacyPiece && !hasArgonPiece;
            });

            AddAssert("lane background is still the legacy one (unaffected by the fix)", () =>
            {
                var playfield = visuals.ChartRenderer!.DrawableRuleset!.Playfield;
                return playfield.ChildrenOfType<osu.Game.Rulesets.Taiko.Skinning.Legacy.TaikoLegacyPlayfieldBackgroundRight>().Any();
            });

            AddStep("remove visuals", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, false);
                Remove(visuals, true);
            });
        }

        // Regression coverage for reopen #6: MainScreen hosts BeatmapVisuals inside a fixed
        // 854×480 (16:9) design canvas (see MainScreen.scene_width/scene_height), NOT the 4:3
        // chartContainer previously used internally. Every earlier taiko test in this
        // investigation added BeatmapVisuals directly into the test scene at its default (roughly
        // 4:3-ish) size, which coincidentally never exposed this: chartContainer's old fixed
        // 1024×768 canvas happened to be close enough in aspect to those tests' own hosting that
        // the mismatch this test targets was invisible. Explicitly hosts BeatmapVisuals inside a
        // 854×480 box — matching MainScreen's real design canvas exactly — and asserts the taiko
        // lane background spans effectively the full width of the SCENE (not just of the
        // playfield's own reported bounds, which the earlier lane-composition test already covers
        // and would trivially "pass" even if the whole playfield were narrower than the box it's
        // hosted in).
        [Test]
        public void TaikoLaneSpansFullSceneWidthIn16By9Box()
        {
            BeatmapVisuals visuals = null!;
            Drawable wrapper = null!;
            var manual = new osu.Framework.Timing.ManualClock();
            osu.Framework.Timing.FramedClock? clock = null;
            const float scene_width = 854;
            const float scene_height = 480;

            AddStep("enable chart", () => config.SetValue(JukeBoxSetting.RenderChart, true));

            AddStep("create visuals inside a 854x480 (16:9) hosting box", () =>
            {
                string osuFile = Path.Combine(tmp, "taiko-widescreen.osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 1

                    [Difficulty]
                    HPDrainRate:5
                    CircleSize:4
                    OverallDifficulty:5
                    ApproachRate:5
                    SliderMultiplier:1.4
                    SliderTickRate:1

                    [TimingPoints]
                    0,500,4,1,0,100,1,0

                    [HitObjects]
                    64,192,5000,1,0
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 16,
                    Directory = tmp,
                    BackgroundFile = fixtureSetA.BackgroundFile,
                    OsuFiles = { osuFile },
                    PreferredOsuFile = osuFile,
                };

                clock = new osu.Framework.Timing.FramedClock(manual);

                // Mirrors MainScreen.sceneContainer: a fixed design-size canvas hosting the real
                // visuals stack, matching the actual production hosting geometry this bug depends on.
                Add(wrapper = new osu.Framework.Graphics.Containers.Container
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Size = new osuTK.Vector2(scene_width, scene_height),
                    Child = visuals = new BeatmapVisuals(set, clock) { RelativeSizeAxes = Axes.Both },
                });
            });

            AddUntilStep("chart loaded", () => visuals.IsLoaded && visuals.HasChartLayer && visuals.ChartRenderer?.DrawableRuleset != null);

            AddStep("advance to just before the hit", () =>
            {
                manual.CurrentTime = 4990;
                clock!.ProcessFrame();
            });

            AddUntilStep("note visible", () =>
                visuals.ChartRenderer!.DrawableRuleset!.Playfield.AllHitObjects
                       .OfType<osu.Game.Rulesets.Taiko.Objects.Drawables.DrawableHit>().Any());

            AddAssert("taiko lane background spans >=99.5% of the scene's own width", () =>
            {
                var playfield = visuals.ChartRenderer!.DrawableRuleset!.Playfield;
                var laneBg = laneBackground(playfield);
                if (laneBg == null)
                    return false;

                float sceneWidth = visuals.ScreenSpaceDrawQuad.TopRight.X - visuals.ScreenSpaceDrawQuad.TopLeft.X;
                var laneQuad = laneBg.ScreenSpaceDrawQuad;
                float laneWidth = laneQuad.TopRight.X - laneQuad.TopLeft.X;

                return laneWidth >= sceneWidth * 0.995f;
            });

            AddStep("remove visuals", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, false);
                Remove(wrapper, true);
            });
        }

        // Regression coverage for reopen #8: the box (854x480, see MainScreen.scene_width/height)
        // that TaikoLaneSpansFullSceneWidthIn16By9Box above hosts BeatmapVisuals directly inside
        // is itself the actual bug's SOURCE, not a neutral test rig — MainScreen never hands
        // visualsStack a box that shape; it hands it a LIVE playerBox (whatever the actual window
        // gives it) that sceneContainer's fixed 854x480 canvas is then uniformly CONTAIN-fit
        // scaled into, letterboxing on whichever axis has slack. Once that live box is wider than
        // 854/480 (~1.779:1) — the common case on anything 16:9 or wider, or in focus mode where
        // the box goes full-bleed — sceneContainer's own contain-fit left dead margins down both
        // sides of EVERYTHING inside it, taiko lane included, even though taiko's own
        // TaikoPlayfieldAdjustmentContainer never even saw an aspect requiring ITS OWN [5:4,16:9]
        // clamp to fire (confirmed with a real windowed screenshot harness: the storyboard, a
        // chartContainer sibling, showed the exact same margin, proving it wasn't taiko-specific
        // at all). The fix threads the box's REAL live size down via MainScreen's [Cached]
        // playerBoxSize (mirrored here) so chartContainer's aspect matches the box's real aspect
        // instead of the fixed canvas' — this test hosts BeatmapVisuals inside the SAME 854x480
        // scene canvas as above, but with playerBoxSize cached to a box noticeably WIDER than
        // 16:9, and asserts the lane still spans the box's own edges, not just the canvas'.
        [Test]
        public void TaikoLaneReachesTheLiveBoxEdgesWhenTheBoxIsWiderThan16By9()
        {
            BeatmapVisuals visuals = null!;
            Drawable liveBox = null!;
            Drawable sceneContainer = null!;
            var manual = new osu.Framework.Timing.ManualClock();
            osu.Framework.Timing.FramedClock? clock = null;
            const float scene_width = 854;
            const float scene_height = 480;
            var wideBox = new osuTK.Vector2(1900, 1047); // ~1.815:1 — clearly wider than 16:9

            AddStep("enable chart", () => config.SetValue(JukeBoxSetting.RenderChart, true));
            AddStep("cache a live playerBoxSize wider than 16:9", () => playerBoxSize.Value = wideBox);

            AddStep("create visuals inside a live-box -> scene-canvas hierarchy matching MainScreen's real playerBox -> sceneContainer contain-fit", () =>
            {
                string osuFile = Path.Combine(tmp, "taiko-wide-box.osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 1

                    [Difficulty]
                    HPDrainRate:5
                    CircleSize:4
                    OverallDifficulty:5
                    ApproachRate:5
                    SliderMultiplier:1.4
                    SliderTickRate:1

                    [TimingPoints]
                    0,500,4,1,0,100,1,0

                    [HitObjects]
                    64,192,5000,1,0
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 17,
                    Directory = tmp,
                    BackgroundFile = fixtureSetA.BackgroundFile,
                    OsuFiles = { osuFile },
                    PreferredOsuFile = osuFile,
                };

                clock = new osu.Framework.Timing.FramedClock(manual);

                float baseScale = Math.Min(wideBox.X / scene_width, wideBox.Y / scene_height);

                Add(liveBox = new osu.Framework.Graphics.Containers.Container
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Size = wideBox,
                    Child = sceneContainer = new osu.Framework.Graphics.Containers.Container
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Size = new osuTK.Vector2(scene_width, scene_height),
                        Scale = new osuTK.Vector2(baseScale),
                        Child = visuals = new BeatmapVisuals(set, clock) { RelativeSizeAxes = Axes.Both },
                    },
                });
            });

            AddUntilStep("chart loaded", () => visuals.IsLoaded && visuals.HasChartLayer && visuals.ChartRenderer?.DrawableRuleset != null);

            AddStep("advance to just before the hit", () =>
            {
                manual.CurrentTime = 4990;
                clock!.ProcessFrame();
            });

            AddUntilStep("note visible", () =>
                visuals.ChartRenderer!.DrawableRuleset!.Playfield.AllHitObjects
                       .OfType<osu.Game.Rulesets.Taiko.Objects.Drawables.DrawableHit>().Any());

            AddAssert("taiko lane background spans >=99.5% of the LIVE (wider-than-canvas) box width", () =>
            {
                var playfield = visuals.ChartRenderer!.DrawableRuleset!.Playfield;
                var laneBg = laneBackground(playfield);
                if (laneBg == null)
                    return false;

                float liveBoxWidth = liveBox.ScreenSpaceDrawQuad.TopRight.X - liveBox.ScreenSpaceDrawQuad.TopLeft.X;
                var laneQuad = laneBg.ScreenSpaceDrawQuad;
                float laneWidth = laneQuad.TopRight.X - laneQuad.TopLeft.X;

                return laneWidth >= liveBoxWidth * 0.995f;
            });

            AddStep("remove visuals", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, false);
                playerBoxSize.Value = default;
                Remove(liveBox, true);
            });
        }

        // Regression coverage for reopen #7: a prior fix here (LazerChartLayer.
        // unmaskLegacyTaikoDrumFlash, since removed) disabled LegacyHalfDrum's Masking on the
        // theory that some legacy skins ship an oversized drum-flash texture meant to bloom
        // outward past the drum. A follow-up screenshot showed that was wrong — with masking
        // disabled, the flash renders as misshapen, offset crescents spilling past the drum
        // instead of a clean flash, because LegacyHalfDrum's masking isn't cropping an oversized
        // bloom down: each "half" is DESIGNED to show only its own semicircle of a combined flash
        // (Rim/Centre are separately Origin/Scale-flipped per side for exactly this), relying on
        // that masking to keep a plain, unfit Sprite (LegacyHalfDrum applies no scale/fit beyond
        // upstream's own @2x detection) looking correct regardless of the texture's exact
        // proportions — removing it exposes whatever raw, unfit geometry that Sprite actually has.
        // Locks in that Masking must stay enabled (guards against reintroducing exactly this
        // mistake) and separately asserts that, with a 64px file and a 128px "@2x" sibling both
        // present, the drum's sprite ends up displayed at 64 rather than 128.
        //
        // NOTE on what that second assertion does and does not show. It was originally written to
        // check the theory that our BeatmapFolderSkin fails to replicate lazer's "@2x"
        // high-resolution texture detection (LegacySkin.GetTexture: an "x@2x.png" sibling is served
        // in place of "x" at ScaleAdjust 2, halving its displayed size). It cannot actually
        // distinguish that: 64px at ScaleAdjust 1 and 128px at ScaleAdjust 2 both display at 64, so
        // the assertion holds either way. It has since turned out that lazer's beatmap skins
        // deliberately DISABLE that detection (LegacyBeatmapSkin.AllowHighResolutionSprites =>
        // false — @2x is a skin convention, not a beatmap one), and BeatmapFolderSkin now mirrors
        // that. The assertion below is still worth keeping as a display-size regression guard, but
        // the behaviour it was aimed at is pinned properly, on the RAW texture, in
        // TestSceneBeatmapFolderSkin.
        [Test]
        public void TaikoDrumFlashMaskingStaysEnabledAndAnAt2xPairDisplaysAtOneXSize()
        {
            BeatmapVisuals visuals = null!;
            var manual = new osu.Framework.Timing.ManualClock();
            osu.Framework.Timing.FramedClock? clock = null;

            AddStep("enable chart, Classic skin", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, true);
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Classic);
            });

            AddStep("create visuals with a legacy beatmap skin providing an @2x drum flash texture", () =>
            {
                string mapDir = Path.Combine(tmp, "drumflash-at2x");
                Directory.CreateDirectory(mapDir);

                // A 64x64 "1x" texture and a DIFFERENT-content 128x128 "@2x" sibling for the same
                // component — if @2x detection works, the displayed sprite matches the 1x file's
                // 64x64 size (ScaleAdjust halves the higher-resolution asset), not 128x128 raw.
                File.WriteAllBytes(Path.Combine(mapDir, "taiko-drum-outer.png"), solidPng(64, 64));
                File.WriteAllBytes(Path.Combine(mapDir, "taiko-drum-outer@2x.png"), solidPng(128, 128));
                File.WriteAllBytes(Path.Combine(mapDir, "taiko-drum-inner.png"), solidPng(64, 64));
                File.WriteAllBytes(Path.Combine(mapDir, "taiko-bar-left.png"), solidPng(64, 64));
                File.WriteAllBytes(Path.Combine(mapDir, "bg.png"), solidPng(4, 4));

                string osuFile = Path.Combine(mapDir, "taiko [x].osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 1

                    [Difficulty]
                    HPDrainRate:5
                    CircleSize:4
                    OverallDifficulty:5
                    ApproachRate:5
                    SliderMultiplier:1.4
                    SliderTickRate:1

                    [TimingPoints]
                    0,500,4,1,0,100,1,0

                    [HitObjects]
                    64,192,5000,1,0
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 18,
                    Directory = mapDir,
                    BackgroundFile = Path.Combine(mapDir, "bg.png"),
                    OsuFiles = { osuFile },
                    PreferredOsuFile = osuFile,
                };

                clock = new osu.Framework.Timing.FramedClock(manual);
                Add(visuals = new BeatmapVisuals(set, clock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("chart loaded", () => visuals.IsLoaded && visuals.HasChartLayer && visuals.ChartRenderer?.DrawableRuleset != null);

            AddStep("advance past the hit", () =>
            {
                manual.CurrentTime = 5010;
                clock!.ProcessFrame();
            });

            AddAssert("both half-drum containers found", () =>
            {
                var playfield = visuals.ChartRenderer!.DrawableRuleset!.Playfield;
                return playfield.ChildrenOfType<osu.Framework.Graphics.Containers.Container>()
                                .Count(c => c.Name is "Left Half" or "Right Half") == 2;
            });

            AddAssert("half-drum containers keep Masking enabled (matches upstream; do not disable it)", () =>
            {
                var playfield = visuals.ChartRenderer!.DrawableRuleset!.Playfield;
                return playfield.ChildrenOfType<osu.Framework.Graphics.Containers.Container>()
                                .Where(c => c.Name is "Left Half" or "Right Half")
                                .All(c => c.Masking);
            });

            AddAssert("the @2x drum-outer texture displays at its 1x sibling's size, not its own raw size", () =>
            {
                var playfield = visuals.ChartRenderer!.DrawableRuleset!.Playfield;
                var half = playfield.ChildrenOfType<osu.Framework.Graphics.Containers.Container>().First(c => c.Name is "Left Half" or "Right Half");
                var sprite = half.ChildrenOfType<osu.Framework.Graphics.Sprites.Sprite>().FirstOrDefault(s => s.Texture != null);

                return sprite?.Texture != null && Math.Abs(sprite.Texture.DisplayWidth - 64) < 1f;
            });

            AddStep("remove visuals", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, false);
                Remove(visuals, true);
            });
        }

        // Regression coverage for reopen #8, this time confirmed via a real windowed screenshot
        // harness (TakeScreenshotAsync, a genuine GameHost window — headless geometry assertions
        // alone had been exhausted across several prior rounds): a beatmap-provided legacy taiko
        // skin's drum hit-flash (LegacyHalfDrum's Rim/Centre sprites) rendered as a half-disc
        // hanging off the BOTTOM of the drum's own box on every hit.
        //
        // Root cause: LegacyHalfDrum.load() branches its Rim/Centre Position calibration on
        // `skin.GetConfig<LegacySetting, decimal>(LegacySetting.Version)?.Value >= 2.1m` — the
        // modern (>=2.1) branch has no extra Y offset; the pre-2.1 branch adds one
        // (`taiko_bar_y + 31`, times the class's own ratio=1.6 constant = +49.6 local units
        // downward). Real lazer's LegacyBeatmapSkin deliberately overrides GetConfig to always
        // return null for LegacySetting.Version specifically ("ignore beatmap-level versioning
        // completely" — its own comment notes the legacy decoder defaults an UNSPECIFIED version to
        // 1.0, so a naive beatmap-skin lookup would always report a version, never fall through to
        // the user's actual skin). Our BeatmapFolderSkin (the standalone equivalent — real
        // LegacyBeatmapSkin can't be used directly, it's realm-backed) had no such override, so it
        // always answered "1.0" (the .osu-file-as-skin-config parse's default) — permanently
        // forcing the OLD, sub-2.1 positioning for the drum flash on EVERY beatmap-provided legacy
        // taiko skin, regardless of the user's actual (typically modern, >=2.1) selected skin.
        //
        // BeatmapFolderSkin.GetConfig now mirrors LegacyBeatmapSkin's override exactly.
        [Test]
        public void TaikoBeatmapSkinDrumFlashStaysWithinTheDrumsOwnBounds()
        {
            BeatmapVisuals visuals = null!;
            var manual = new osu.Framework.Timing.ManualClock();
            osu.Framework.Timing.FramedClock? clock = null;

            AddStep("enable chart, Classic skin", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, true);
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Classic);
            });

            AddStep("create visuals with a beatmap-provided legacy drum skin", () =>
            {
                string mapDir = Path.Combine(tmp, "drumflash-version");
                Directory.CreateDirectory(mapDir);

                File.WriteAllBytes(Path.Combine(mapDir, "taiko-bar-left.png"), solidPng(64, 64));
                File.WriteAllBytes(Path.Combine(mapDir, "taiko-drum-outer.png"), solidPng(96, 96));
                File.WriteAllBytes(Path.Combine(mapDir, "taiko-drum-inner.png"), solidPng(80, 80));
                File.WriteAllBytes(Path.Combine(mapDir, "bg.png"), solidPng(4, 4));

                string osuFile = Path.Combine(mapDir, "taiko [x].osu");
                File.WriteAllText(osuFile, """
                    osu file format v14

                    [General]
                    AudioFilename: audio.mp3
                    Mode: 1

                    [Difficulty]
                    HPDrainRate:5
                    CircleSize:4
                    OverallDifficulty:5
                    ApproachRate:5
                    SliderMultiplier:1.4
                    SliderTickRate:1

                    [TimingPoints]
                    0,500,4,1,0,100,1,0

                    [HitObjects]
                    64,192,5000,1,0
                    """);

                var set = new CachedBeatmapSet
                {
                    SetId = 19,
                    Directory = mapDir,
                    BackgroundFile = Path.Combine(mapDir, "bg.png"),
                    OsuFiles = { osuFile },
                    PreferredOsuFile = osuFile,
                };

                clock = new osu.Framework.Timing.FramedClock(manual);
                Add(visuals = new BeatmapVisuals(set, clock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("chart loaded", () => visuals.IsLoaded && visuals.HasChartLayer && visuals.ChartRenderer?.DrawableRuleset != null);

            // Drive PAST the hit (not just to it) so autoplay's replay actually presses the drum —
            // LegacyHalfDrum.OnPressed, driven through the real input pipeline, is what starts the
            // flash's fade-in/position in the first place.
            AddStep("advance past the hit", () =>
            {
                manual.CurrentTime = 5010;
                clock!.ProcessFrame();
            });

            AddAssert("both half-drum containers found", () =>
            {
                var playfield = visuals.ChartRenderer!.DrawableRuleset!.Playfield;
                return playfield.ChildrenOfType<osu.Framework.Graphics.Containers.Container>()
                                .Count(c => c.Name is "Left Half" or "Right Half") == 2;
            });

            // Checks the sprites' own LOCAL Y position directly rather than a screen-space
            // containment box: LegacyHalfDrum's two position calibrations differ by a fixed, small
            // local-unit offset (taiko_bar_y=0 vs. taiko_bar_y+31, times its own ratio=1.6 constant
            // = 0 vs. +49.6 for Centre, 0 vs. +36.8 for Rim) that's independent of the drum's
            // overall on-screen scale or the flash texture's own pixel size — unlike a screen-space
            // "does it exceed this box" check, which needs a large-enough texture/small-enough
            // playfield to ever actually cross a boundary, and so isn't a reliable regression guard
            // on its own.
            AddAssert("drum flash sprites use the modern (Y=0-based) position calibration, not the pre-2.1 one", () =>
            {
                var playfield = visuals.ChartRenderer!.DrawableRuleset!.Playfield;

                var halves = playfield.ChildrenOfType<osu.Framework.Graphics.Containers.Container>()
                                       .Where(c => c.Name is "Left Half" or "Right Half");

                const float tolerance = 5f;

                foreach (var half in halves)
                {
                    foreach (var sprite in half.ChildrenOfType<osu.Framework.Graphics.Sprites.Sprite>())
                    {
                        if (Math.Abs(sprite.Y) > tolerance)
                            return false;
                    }
                }

                return true;
            });

            AddStep("remove visuals", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, false);
                Remove(visuals, true);
            });
        }

        // 1x1 red pixel PNG — content is irrelevant, only that it decodes to a valid texture.
        private static byte[] solidPng() => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        // Solid-colour PNG at an explicit size, for texture-scale tests where dimensions matter.
        private static byte[] solidPng(int width, int height)
        {
            using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height,
                new SixLabors.ImageSharp.PixelFormats.Rgba32(255, 128, 0, 255));
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }
    }
}
