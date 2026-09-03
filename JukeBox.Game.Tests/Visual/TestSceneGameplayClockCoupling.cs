#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Playback;
using JukeBox.Game.Screens;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Whether gameplay actually follows the app's playback clock, driven by the REAL
    /// <see cref="PlaybackController"/> rather than a hand-held test clock.
    ///
    /// <para>
    /// This exists because a test harness said it did not. Driving a chart layer from a static
    /// ManualClock, gameplay was seen running on by itself — reaching 14 seconds of play while the
    /// driving clock read zero — and a big seek reported its snap as engaged while leaving gameplay
    /// crawling 127 seconds behind. Both would be serious for someone who pauses and seeks
    /// constantly, so they get checked against the clock the app really uses instead of the one
    /// that produced the symptom.
    /// </para>
    /// </summary>
    [TestFixture]
    public partial class TestSceneGameplayClockCoupling : JukeBoxTestScene
    {
        private string tmp = null!;
        private string beatmapPath = null!;
        private BeatmapVisuals visuals = null!;

        [Resolved]
        private PlaybackController playback { get; set; } = null!;

        private JukeBox.Game.Beatmaps.CachedBeatmapSet set = null!;

        [SetUp]
        public void SetUp() => Schedule(() =>
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            beatmapPath = Path.Combine(tmp, "map [Hard].osu");
            File.WriteAllText(beatmapPath, map());

            set = new JukeBox.Game.Beatmaps.CachedBeatmapSet
            {
                SetId = 1,
                Directory = tmp,
                PreferredOsuFile = beatmapPath,
                OsuFiles = { beatmapPath },

                // A silent virtual track, which is what the app itself falls back to for a map with
                // no audio file. It still runs, still seeks and still pauses — the clock is the
                // thing under test here, not the sound.
                HasVirtualAudio = true,
            };

            Clear();
        });

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(tmp))
                    Directory.Delete(tmp, true);
            }
            catch (IOException)
            {
            }
        }

        private LazerChartLayer chart => visuals.ChildrenOfType<LazerChartLayer>().First();

        private void buildAndPlay()
        {
            AddStep("start playback", () => playback.PlayAsync(set).ConfigureAwait(false));
            AddUntilStep("track running", () => playback.PlaybackClock.IsRunning);

            AddStep("build the visuals on the app's own clock", () => Add(visuals = new BeatmapVisuals(set, playback.PlaybackClock)
            {
                RelativeSizeAxes = Axes.Both,
            }));

            AddUntilStep("chart built", () => visuals.IsLoaded && visuals.ChartLayerBuilt);
            AddUntilStep("gameplay exists", () => chart.FrameStableTime != null);
        }

        /// <summary>
        /// PAUSE. If gameplay carried on while the song was stopped, the chart would drift ahead of
        /// the music every time the user paused — the first of the two predictions.
        /// </summary>
        [Test]
        public void GameplayStopsWhenPlaybackIsPaused()
        {
            buildAndPlay();

            AddUntilStep("gameplay is moving", () => chart.FrameStableTime > 0);

            double pausedAt = 0;

            AddStep("pause", () =>
            {
                playback.TogglePause();
                pausedAt = chart.FrameStableTime ?? 0;
            });

            AddUntilStep("the clock has stopped", () => !playback.PlaybackClock.IsRunning);

            // Real frames have to pass, or "it did not advance" is only saying nothing was updated.
            AddWaitStep("let real time pass", 30);

            AddAssert("gameplay did not run on", () =>
                Math.Abs((chart.FrameStableTime ?? 0) - pausedAt) < 200);
        }

        /// <summary>
        /// SEEK. If gameplay crawled to catch up instead of snapping, every scrub would be followed
        /// by seconds of fast-forward — the second prediction.
        /// </summary>
        [Test]
        public void GameplaySnapsToABigSeekRatherThanCrawling()
        {
            buildAndPlay();

            AddStep("seek forward 30 seconds", () => playback.Seek(30000));

            // The layer instruments its own catch-up: this is the number of layer updates it took
            // for gameplay to come back within 200ms of the driving clock.
            AddUntilStep("gameplay caught up", () => chart.LastSeekCatchupFrames >= 0);

            AddAssert("and it snapped rather than crawling", () =>
            {
                // A crawl is measured in dozens of frames — a 30s jump took 63 of them before the
                // snap existed. A handful means it jumped.
                return chart.LastSeekCatchupFrames <= 10;
            });

            AddAssert("gameplay is where the clock is", () =>
                Math.Abs((chart.FrameStableTime ?? 0) - playback.PlaybackClock.CurrentTime) < 500);
        }

        private static string map()
        {
            var sb = new StringBuilder();

            sb.Append("osu file format v14\n\n[General]\nAudioFilename: audio.wav\nMode: 0\n\n");
            sb.Append("[Metadata]\nTitle:Clock\nArtist:A\nCreator:C\nVersion:Hard\n\n");
            sb.Append("[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:8\nApproachRate:9\nSliderMultiplier:1.4\nSliderTickRate:1\n\n");
            sb.Append("[TimingPoints]\n0,500,4,2,0,60,1,0\n\n[HitObjects]\n");

            for (int i = 0; i < 200; i++)
                sb.Append($"{100 + i * 37 % 350},{80 + i * 53 % 240},{1000 + i * 400},1,0,0:0:0:0:\n");

            return sb.ToString();
        }
    }
}
