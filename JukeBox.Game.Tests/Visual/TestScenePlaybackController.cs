using System.IO;
using System.Text;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Playback;
using NUnit.Framework;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    [TestFixture]
    public partial class TestScenePlaybackController : JukeBoxTestScene
    {
        private PlaybackController controller = null!;
        private string tmp = null!;
        private CachedBeatmapSet fixtureSet = null!;
        private CachedBeatmapSet fixtureSetA = null!;
        private CachedBeatmapSet fixtureSetB = null!;

        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            string audioFile = Path.Combine(tmp, "audio.wav");
            writeSilentWav(audioFile, 1);

            fixtureSet = new CachedBeatmapSet
            {
                SetId = 1,
                Directory = tmp,
                AudioFile = audioFile,
            };

            // Two more fixtures in their own subdirectories, for the overlapping-PlayAsync test.
            string dirA = Path.Combine(tmp, "a");
            string dirB = Path.Combine(tmp, "b");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            string audioFileA = Path.Combine(dirA, "audio.wav");
            string audioFileB = Path.Combine(dirB, "audio.wav");
            writeSilentWav(audioFileA, 1);
            writeSilentWav(audioFileB, 1);

            fixtureSetA = new CachedBeatmapSet { SetId = 10, Directory = dirA, AudioFile = audioFileA };
            fixtureSetB = new CachedBeatmapSet { SetId = 20, Directory = dirB, AudioFile = audioFileB };
        }

        // NOTE: deliberately NOT deleting `tmp` here. TestScene runs each [Test] method's queued
        // AddStep bodies from a base-class teardown hook that NUnit invokes *after* this derived
        // class's own [TearDown] — so a synchronous delete here would race the fixture files out
        // from under the still-pending steps (confirmed empirically: it caused every track load
        // in this file to silently fail). Test temp dirs are left for the OS to reclaim, matching
        // the same tradeoff already made by TestScene's own step/browser scratch directories.

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create controller", () => Child = controller = new PlaybackController());
        }

        [Test]
        public void PlayThenPauseStopsClock()
        {
            AddStep("play fixture", () => controller.PlayAsync(fixtureSet));
            AddUntilStep("track is active", () => controller.Current.Value?.SetId == fixtureSet.SetId);
            AddUntilStep("clock advances", () => controller.CurrentTimeMs > 0);

            AddStep("pause", () => controller.TogglePause());
            AddAssert("not playing", () => !controller.IsPlaying);
        }

        // Regression test for the PlayAsync overlap race: the most-recently-requested call must
        // win the swap even if its load happens to finish before (or after) the older call's.
        [Test]
        public void OverlappingPlayAsyncSecondCallWins()
        {
            AddStep("play A then B back-to-back", () =>
            {
                controller.PlayAsync(fixtureSetA);
                controller.PlayAsync(fixtureSetB);
            });

            AddUntilStep("second call's track is active", () => controller.Current.Value?.SetId == fixtureSetB.SetId);
            AddAssert("first call never became active", () => controller.Current.Value?.SetId != fixtureSetA.SetId);
        }

        // BASS (the audio backend behind osu!framework's Track) plays WAV directly, so a
        // hand-written 44-byte RIFF header followed by silence is enough to drive playback.
        private static void writeSilentWav(string path, double seconds)
        {
            const int sample_rate = 44100;
            const short channels = 1;
            const short bits_per_sample = 16;

            int dataSize = (int)(sample_rate * channels * (bits_per_sample / 8) * seconds);

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);

            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write(channels);
            writer.Write(sample_rate);
            writer.Write(sample_rate * channels * (bits_per_sample / 8));
            writer.Write((short)(channels * (bits_per_sample / 8)));
            writer.Write(bits_per_sample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            writer.Write(new byte[dataSize]);
        }
    }
}
