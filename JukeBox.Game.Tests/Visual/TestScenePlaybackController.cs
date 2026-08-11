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
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, true);
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create controller", () => Child = controller = new PlaybackController());
        }

        [Test]
        public void PlayThenPauseStopsClock()
        {
            AddStep("play fixture", () => controller.PlayAsync(fixtureSet));
            AddUntilStep("clock advances", () => controller.CurrentTimeMs > 0);

            AddStep("pause", () => controller.TogglePause());
            AddAssert("not playing", () => !controller.IsPlaying);
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
