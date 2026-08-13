#nullable enable

using System;
using System.IO;
using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.Playback;
using JukeBox.Game.Screens;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Screens;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Game.Rulesets.Catch;

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

        // Regression coverage for the catch (CTB) crop bug: unlike standard/taiko/mania, whose
        // lazer PlayfieldAdjustmentContainers size themselves in RELATIVE fractions of whatever
        // box they're given (scale-invariant), catch's CatchPlayfieldAdjustmentContainer uses
        // ABSOLUTE pixel constants (a 1024×768 "base game" canvas, +200 reserved below for the
        // catcher plate) calibrated against lazer's own Player-screen convention
        // (DrawSizePreservingFillContainer.TargetSize) — chartContainer must actually BE a
        // 1024×768 canvas (see its construction comment in BeatmapVisuals) or catch renders ~2x
        // oversized and the catcher/fruits end up entirely outside the box. Playfield's own
        // ScreenSpaceDrawQuad is asserted (not just the DrawableRuleset's, which is
        // RelativeSizeAxes-filled and would trivially "fit") because catch's internal
        // ScalingContainer deliberately grows Playfield's own bounds to cover the catcher —
        // that's the geometry lazer actually renders on screen.
        [Test]
        public void CatchPlayfieldIncludingCatcherFitsInsideTheVisualsBox()
        {
            BeatmapVisuals visuals = null!;

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

                Add(visuals = new BeatmapVisuals(set, playbackClock) { RelativeSizeAxes = Axes.Both });
            });

            AddUntilStep("chart loaded", () => visuals.IsLoaded && visuals.HasChartLayer && visuals.ChartRenderer?.DrawableRuleset != null);
            AddAssert("catch ruleset chosen", () => visuals.ChartRenderer!.Ruleset?.GetType() == typeof(CatchRuleset));

            AddAssert("catch playfield (including the catcher) fits entirely within the visuals box", () =>
            {
                var box = visuals.ScreenSpaceDrawQuad;
                var playfield = visuals.ChartRenderer!.DrawableRuleset!.Playfield.ScreenSpaceDrawQuad;

                return playfield.TopLeft.X >= box.TopLeft.X - 0.5f && playfield.TopRight.X <= box.TopRight.X + 0.5f
                       && playfield.TopLeft.Y >= box.TopLeft.Y - 0.5f && playfield.BottomLeft.Y <= box.BottomLeft.Y + 0.5f;
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

        // 1x1 red pixel PNG — content is irrelevant, only that it decodes to a valid texture.
        private static byte[] solidPng() => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    }
}
