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

        private string writeDiff(string name, string version)
        {
            string path = Path.Combine(tmp, name);
            File.WriteAllText(path,
                $"osu file format v14\n\n[General]\nAudioFilename: audio.mp3\nMode: 0\n\n[Metadata]\nVersion:{version}\n\n" +
                "[Difficulty]\nCircleSize:4\nApproachRate:9\nSliderMultiplier:1.4\n\n[TimingPoints]\n0,500,4,1,0,100,1,0\n\n[HitObjects]\n256,192,1000,1,0\n");
            return path;
        }

        // NOTE: deliberately NOT deleting `tmp` in teardown — see TestScenePlaybackController for
        // why (queued steps still run after this class's [TearDown] fires).

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
    }
}
