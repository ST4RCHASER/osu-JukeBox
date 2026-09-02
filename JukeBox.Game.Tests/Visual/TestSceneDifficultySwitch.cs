#nullable enable

using System.Collections.Generic;
using System.IO;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Playback;
using JukeBox.Game.Screens;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Screens;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Difficulty switching mid-playback: changing <see cref="PlaybackController.SelectedOsuFile"/>
    /// must rebuild <see cref="NowPlayingScreen"/>'s visual stack for the new difficulty (and
    /// dispose the old one) while the shared clock — and therefore the playback position — is
    /// left untouched.
    /// </summary>
    [TestFixture]
    public partial class TestSceneDifficultySwitch : JukeBoxTestScene
    {
        [Cached]
        private readonly PlaybackController controller = new PlaybackController();

        private string tmp = null!;
        private CachedBeatmapSet fixtureSet = null!;
        private string easyDiff = null!;
        private string hardDiff = null!;

        private double timeBeforeSwitch;

        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            easyDiff = writeDiff("easy.osu", "Easy");
            hardDiff = writeDiff("hard.osu", "Hard");

            fixtureSet = new CachedBeatmapSet
            {
                SetId = 1,
                Directory = tmp,
                OsuFiles = new List<string> { easyDiff, hardDiff },
                PreferredOsuFile = easyDiff,
                Difficulties = new List<DifficultyInfo>
                {
                    new DifficultyInfo { Path = easyDiff, Version = "Easy", Mode = 0, AudioFilename = "audio.mp3" },
                    new DifficultyInfo { Path = hardDiff, Version = "Hard", Mode = 0, AudioFilename = "audio.mp3" },
                },
            };
        }

        private string writeDiff(string name, string version, string? events = null)
        {
            string path = Path.Combine(tmp, name);
            File.WriteAllText(path,
                $"osu file format v14\n\n[General]\nAudioFilename: audio.mp3\nMode: 0\n\n[Metadata]\nVersion:{version}\n\n" +
                $"[Events]\n{events}\n\n" +
                "[Difficulty]\nCircleSize:4\nApproachRate:9\nSliderMultiplier:1.4\n\n[TimingPoints]\n0,500,4,1,0,100,1,0\n\n[HitObjects]\n256,192,1000,1,0\n");
            return path;
        }

        /// <summary>
        /// Writes a solid PNG of an explicit size into the fixture directory. Sizes differ per
        /// difficulty so a test can tell WHICH file the background sprite actually decoded, not
        /// just which path was resolved.
        /// </summary>
        private string writeBackground(string name, int size)
        {
            string path = Path.Combine(tmp, name);

            using (var image = new Image<Rgba32>(size, size, new Rgba32(255, 128, 0, 255)))
            using (var stream = File.Create(path))
                image.SaveAsPng(stream);

            return path;
        }

        private CachedBeatmapSet buildSet(int setId, string preferred, string? setBackground, params string[] diffs)
            => new CachedBeatmapSet
            {
                SetId = setId,
                Directory = tmp,
                BackgroundFile = setBackground,
                OsuFiles = new List<string>(diffs),
                PreferredOsuFile = preferred,
            };

        // NOTE: deliberately NOT deleting `tmp` in teardown — see TestScenePlaybackController for
        // why (queued steps still run after this class's [TearDown] fires).

        /// <summary>
        /// The reported bug: on a song change the audio swapped immediately but the PREVIOUS song's
        /// storyboard and chart kept running for seconds, because the outgoing stack was only taken
        /// off screen once the incoming one had finished loading — a load that is seconds long on a
        /// storyboard-heavy set. Whatever else the swap does, the old stack has to stop being what
        /// the user is watching (and listening to) the moment the track changes.
        /// </summary>
        [Test]
        public void TheOutgoingStackStopsTheMomentTheTrackChanges()
        {
            NowPlayingScreen screen = null!;
            ScreenStack stack = null!;
            BeatmapVisuals? first = null;

            AddStep("reset selection", () =>
            {
                controller.Current.Value = null;
                controller.SelectedOsuFile.Value = null;
            });

            AddStep("create screen", () => Add(stack = new ScreenStack(screen = new NowPlayingScreen())
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddStep("play the first set", () => controller.Current.Value = fixtureSet);
            AddUntilStep("its visuals are on screen",
                () => screen.CurrentVisuals?.IsLoaded == true && screen.CurrentVisuals.Alpha == 1);
            AddStep("capture them", () => first = screen.CurrentVisuals);

            // Same frame as the change, deliberately: this is the whole point. The assertion runs in
            // the step immediately after, before any load can have completed.
            AddStep("change song", () => controller.Current.Value = buildSet(2, hardDiff, null, hardDiff));

            AddAssert("the old stack is retired at once", () => first!.Retired);
            AddAssert("and silent — hitsounds off, and no longer kept alive to sound while hidden",
                () => !first!.HasHitSoundPlayer && !first.ChartLayerAlwaysPresent
                      && first.AudioAdjustments.Volume.Value == 0);
            AddAssert("and off screen in that same frame, not once the new one loads", () => first!.Alpha == 0);
            // Not present is what actually stops it animating: osu!framework skips the update
            // subtree of a drawable that is not present (the same rule the hidden-chart hitsounds
            // needed AlwaysPresent to escape, which Retire clears).
            AddAssert("and no longer updating at all", () => !first!.IsPresent);

            AddUntilStep("the new stack arrives and is not retired",
                () => screen.CurrentVisuals != null && screen.CurrentVisuals != first
                      && screen.CurrentVisuals.IsLoaded && !screen.CurrentVisuals.Retired);
            AddAssert("the old one is gone entirely", () => first!.Disposed);

            AddStep("remove screen", () => Remove(stack, true));
        }

        /// <summary>
        /// A song change moves two bindables — the set and the difficulty selection — and used to
        /// cost two full builds, the first thrown away for being a generation behind while both
        /// competed for the same load threads. One change, one build.
        /// </summary>
        [Test]
        public void ASongChangeBuildsExactlyOneStack()
        {
            NowPlayingScreen screen = null!;
            ScreenStack stack = null!;
            int buildsBefore = 0;

            AddStep("reset selection", () =>
            {
                controller.Current.Value = null;
                controller.SelectedOsuFile.Value = null;
            });

            AddStep("create screen", () => Add(stack = new ScreenStack(screen = new NowPlayingScreen())
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddStep("play the first set", () => controller.Current.Value = fixtureSet);
            AddUntilStep("its visuals are on screen", () => screen.CurrentVisuals?.IsLoaded == true);
            AddStep("note the build count", () => buildsBefore = screen.Builds);

            // The controller sets these as a pair, and the difficulty is NOT the set's preferred
            // one — the case that used to produce a second build.
            AddStep("change song, selecting a non-preferred difficulty", () =>
            {
                controller.Current.Value = buildSet(4, easyDiff, null, easyDiff, hardDiff);
                controller.SelectedOsuFile.Value = hardDiff;
            });

            AddUntilStep("the new stack is on screen",
                () => screen.CurrentVisuals?.IsLoaded == true && screen.CurrentVisuals.OsuFile == hardDiff);
            AddAssert("and it cost exactly one build", () => screen.Builds - buildsBefore == 1);

            AddStep("remove screen", () => Remove(stack, true));
        }

        [Test]
        public void SwitchingDifficultyRebuildsVisualsWithoutTouchingClock()
        {
            NowPlayingScreen screen = null!;
            ScreenStack stack = null!;

            AddStep("reset selection", () =>
            {
                controller.Current.Value = null;
                controller.SelectedOsuFile.Value = null;
            });

            AddStep("create screen", () => Add(stack = new ScreenStack(screen = new NowPlayingScreen())
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddStep("play set", () => controller.Current.Value = fixtureSet);
            AddUntilStep("easy visuals loaded", () => screen.CurrentVisuals?.IsLoaded == true && screen.CurrentVisuals.OsuFile == easyDiff);

            BeatmapVisuals? firstVisuals = null;
            AddStep("capture visuals", () => firstVisuals = screen.CurrentVisuals);

            AddStep("switch to Hard", () =>
            {
                timeBeforeSwitch = controller.CurrentTimeMs;
                controller.SelectedOsuFile.Value = hardDiff;
            });
            AddUntilStep("hard visuals loaded (new instance)",
                () => screen.CurrentVisuals != null && screen.CurrentVisuals != firstVisuals
                      && screen.CurrentVisuals.IsLoaded && screen.CurrentVisuals.OsuFile == hardDiff);

            AddAssert("old visuals disposed", () => firstVisuals!.Disposed);
            AddAssert("clock position untouched", () => controller.CurrentTimeMs == timeBeforeSwitch);

            AddStep("remove screen", () => Remove(stack, true));
        }

        [Test]
        public void SelectedFileFromAnotherSetFallsBackToPreferred()
        {
            NowPlayingScreen screen = null!;
            ScreenStack stack = null!;

            AddStep("reset", () =>
            {
                controller.Current.Value = null;
                controller.SelectedOsuFile.Value = null;
            });

            AddStep("create screen", () => Add(stack = new ScreenStack(screen = new NowPlayingScreen())
            {
                RelativeSizeAxes = Axes.Both,
            }));

            // A selection that doesn't belong to the current set (e.g. stale from the previous
            // track mid-swap) must not be honoured.
            AddStep("play set with foreign selection", () =>
            {
                controller.SelectedOsuFile.Value = Path.Combine(tmp, "not-in-set.osu");
                controller.Current.Value = fixtureSet;
            });

            AddUntilStep("falls back to preferred diff",
                () => screen.CurrentVisuals?.IsLoaded == true && screen.CurrentVisuals.OsuFile == easyDiff);

            AddStep("remove screen", () => Remove(stack, true));
        }

        /// <summary>
        /// Regression coverage for the per-difficulty background bug: each .osu declares its own
        /// [Events] background, but the set-level <see cref="CachedBeatmapSet.BackgroundFile"/> is
        /// scanned once off the DEFAULT difficulty — so a mid-song difficulty switch used to rebuild
        /// the whole visual stack for the new diff while still showing the old diff's image.
        /// </summary>
        [Test]
        public void SwitchingDifficultySwitchesBackgroundToTheNewDiffs()
        {
            NowPlayingScreen screen = null!;
            ScreenStack stack = null!;

            string easyBg = null!;
            string hardBg = null!;
            string easyWithBg = null!;
            string hardWithBg = null!;
            CachedBeatmapSet set = null!;

            AddStep("write per-diff backgrounds", () =>
            {
                easyBg = writeBackground("bg-easy.png", 4);
                hardBg = writeBackground("bg-hard.png", 16);

                easyWithBg = writeDiff("easy-bg.osu", "Easy", "0,0,\"bg-easy.png\",0,0");
                hardWithBg = writeDiff("hard-bg.osu", "Hard", "0,0,\"bg-hard.png\",0,0");

                // Set-level background deliberately the default (Easy) diff's one, exactly as
                // BeatmapCache computes it — the bug was reading this instead of the selection.
                set = buildSet(11, easyWithBg, easyBg, easyWithBg, hardWithBg);
            });

            AddStep("reset selection", () =>
            {
                controller.Current.Value = null;
                controller.SelectedOsuFile.Value = null;
            });

            AddStep("create screen", () => Add(stack = new ScreenStack(screen = new NowPlayingScreen())
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddStep("play set", () => controller.Current.Value = set);
            AddUntilStep("easy visuals loaded", () => screen.CurrentVisuals?.IsLoaded == true && screen.CurrentVisuals.OsuFile == easyWithBg);

            AddAssert("easy background resolved", () => screen.CurrentVisuals!.BackgroundFile == easyBg);
            AddAssert("easy background decoded (4px)", () => screen.CurrentVisuals!.BackgroundTexture?.Width == 4);
            AddAssert("background visible", () => screen.CurrentVisuals!.BackgroundVisible);

            AddStep("switch to Hard", () => controller.SelectedOsuFile.Value = hardWithBg);
            AddUntilStep("hard visuals loaded", () => screen.CurrentVisuals?.IsLoaded == true && screen.CurrentVisuals.OsuFile == hardWithBg);

            AddAssert("hard background resolved", () => screen.CurrentVisuals!.BackgroundFile == hardBg);
            AddAssert("hard background decoded (16px)", () => screen.CurrentVisuals!.BackgroundTexture?.Width == 16);
            AddAssert("background visible", () => screen.CurrentVisuals!.BackgroundVisible);

            AddStep("remove screen", () => Remove(stack, true));
        }

        /// <summary>
        /// A difficulty that declares no background of its own — or names one that isn't in the
        /// folder — keeps the set-level image rather than going black.
        /// </summary>
        [Test]
        public void DifficultyWithoutItsOwnBackgroundFallsBackToTheSetDefault()
        {
            NowPlayingScreen screen = null!;
            ScreenStack stack = null!;

            string setBg = null!;
            string withBg = null!;
            string noEvents = null!;
            string missingBg = null!;
            CachedBeatmapSet set = null!;

            AddStep("write diffs", () =>
            {
                setBg = writeBackground("bg-set.png", 8);

                withBg = writeDiff("default-bg.osu", "Default", "0,0,\"bg-set.png\",0,0");
                noEvents = writeDiff("no-events.osu", "NoEvents");
                missingBg = writeDiff("missing-bg.osu", "Missing", "0,0,\"not-on-disk.png\",0,0");

                set = buildSet(12, withBg, setBg, withBg, noEvents, missingBg);
            });

            AddStep("reset selection", () =>
            {
                controller.Current.Value = null;
                controller.SelectedOsuFile.Value = null;
            });

            AddStep("create screen", () => Add(stack = new ScreenStack(screen = new NowPlayingScreen())
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddStep("play set", () => controller.Current.Value = set);
            AddUntilStep("default visuals loaded", () => screen.CurrentVisuals?.IsLoaded == true && screen.CurrentVisuals.OsuFile == withBg);

            AddStep("switch to the diff with no [Events] background", () => controller.SelectedOsuFile.Value = noEvents);
            AddUntilStep("no-events visuals loaded", () => screen.CurrentVisuals?.IsLoaded == true && screen.CurrentVisuals.OsuFile == noEvents);
            AddAssert("falls back to the set background", () => screen.CurrentVisuals!.BackgroundFile == setBg);
            AddAssert("set background decoded (8px)", () => screen.CurrentVisuals!.BackgroundTexture?.Width == 8);

            AddStep("switch to the diff naming a missing file", () => controller.SelectedOsuFile.Value = missingBg);
            AddUntilStep("missing-bg visuals loaded", () => screen.CurrentVisuals?.IsLoaded == true && screen.CurrentVisuals.OsuFile == missingBg);
            AddAssert("still falls back to the set background", () => screen.CurrentVisuals!.BackgroundFile == setBg);
            AddAssert("background still visible", () => screen.CurrentVisuals!.BackgroundVisible);

            AddStep("remove screen", () => Remove(stack, true));
        }

        /// <summary>
        /// Video was always per-difficulty (lazer decodes the storyboard, video event included, out
        /// of the SELECTED .osu — see <see cref="JukeBox.Game.LazerPlayer.LazerStoryboardLayer"/>);
        /// this pins that down so the background fix's per-diff scan can't regress it, and confirms
        /// a video-carrying diff still auto-hides the flat background.
        /// </summary>
        [Test]
        public void SwitchingDifficultySwitchesVideoPresence()
        {
            NowPlayingScreen screen = null!;
            ScreenStack stack = null!;

            string noVideo = null!;
            string withVideo = null!;
            CachedBeatmapSet set = null!;

            AddStep("write diffs", () =>
            {
                string bg = writeBackground("bg-video-test.png", 4);

                noVideo = writeDiff("no-video.osu", "NoVideo", "0,0,\"bg-video-test.png\",0,0");
                withVideo = writeDiff("with-video.osu", "WithVideo", "0,0,\"bg-video-test.png\",0,0\nVideo,0,\"movie.mp4\"");

                set = buildSet(13, noVideo, bg, noVideo, withVideo);
            });

            AddStep("reset selection", () =>
            {
                controller.Current.Value = null;
                controller.SelectedOsuFile.Value = null;
            });

            AddStep("create screen", () => Add(stack = new ScreenStack(screen = new NowPlayingScreen())
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddStep("play set", () => controller.Current.Value = set);
            AddUntilStep("no-video visuals loaded", () => screen.CurrentVisuals?.IsLoaded == true && screen.CurrentVisuals.OsuFile == noVideo);
            AddAssert("no video on this diff", () => !screen.CurrentVisuals!.StoryboardLayer.HasVideo);
            AddAssert("background visible", () => screen.CurrentVisuals!.BackgroundVisible);

            AddStep("switch to the video diff", () => controller.SelectedOsuFile.Value = withVideo);
            AddUntilStep("video visuals loaded", () => screen.CurrentVisuals?.IsLoaded == true && screen.CurrentVisuals.OsuFile == withVideo);
            AddAssert("video picked up from the new diff", () => screen.CurrentVisuals!.StoryboardLayer.HasVideo);

            AddStep("remove screen", () => Remove(stack, true));
        }
    }
}
