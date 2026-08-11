#nullable enable

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    // NOTE: lives under Visual/ with the TestScene* naming convention (not
    // JukeBox.Game.Tests/Playback/JukeboxTest.cs as sketched in the task brief) because Jukebox
    // needs a real PlaybackController, which needs framework context (AudioManager) to load a
    // track — same constraint TestScenePlaybackController documents. Pure-NUnit tests in
    // Playback/ can't provide that; this follows the existing TestScene pattern instead.
    [TestFixture]
    public partial class TestSceneJukebox : JukeBoxTestScene
    {
        private MusicQueue queue = null!;
        private RadioService radio = null!;
        private BeatmapCache cache = null!;
        private PlaybackController playback = null!;
        private Jukebox jukebox = null!;

        private string tmp = null!;
        private FixtureMirror mirror = null!;

        private BeatmapSetInfo set1 = null!;
        private BeatmapSetInfo set2 = null!;
        private BeatmapSetInfo setFailing = null!;
        private BeatmapSetInfo set4 = null!;

        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            mirror = new FixtureMirror();
            mirror.Register(1, makeOsz("one", "audio1.wav"));
            mirror.Register(2, makeOsz("two", "audio2.wav"));
            mirror.Register(4, makeOsz("four", "audio4.wav"));
            // 3 is deliberately unregistered: FixtureMirror throws on DownloadAsync for it.

            set1 = new BeatmapSetInfo { Id = 1, Title = "One" };
            set2 = new BeatmapSetInfo { Id = 2, Title = "Two" };
            setFailing = new BeatmapSetInfo { Id = 3, Title = "Failing" };
            set4 = new BeatmapSetInfo { Id = 4, Title = "Four" };

            queue = new MusicQueue();
            radio = new RadioService(mirror);
            cache = new BeatmapCache(Path.Combine(tmp, "cache"), mirror);
        }

        // NOTE: deliberately NOT deleting `tmp` here — see TestScenePlaybackController for why
        // (TestScene runs queued AddStep bodies from a base-class teardown hook that fires after
        // this derived class's own [TearDown], so a synchronous delete here would race the
        // fixture files out from under still-pending steps).

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create jukebox", () =>
            {
                playback = new PlaybackController();
                jukebox = new Jukebox(queue, radio, cache, playback);
                Children = new Drawable[] { playback, jukebox };
            });
        }

        [Test]
        public void EnqueueWhilePlayingStartsFirstSetThenAdvancesThroughQueue()
        {
            AddStep("enqueue set1", () => jukebox.EnqueueAndMaybePlayAsync(set1));
            AddUntilStep("set1 playing", () => playback.Current.Value?.SetId == set1.Id);

            AddStep("enqueue set2 (idle already false, just queues)", () => jukebox.EnqueueAndMaybePlayAsync(set2));
            AddStep("advance", () => jukebox.AdvanceAsync());
            AddUntilStep("set2 playing", () => playback.Current.Value?.SetId == set2.Id);

            AddStep("queue failing set then set4", () =>
            {
                queue.Enqueue(setFailing);
                queue.Enqueue(set4);
            });
            AddStep("advance past failure", () => jukebox.AdvanceAsync());
            AddUntilStep("set4 playing", () => playback.Current.Value?.SetId == set4.Id);
            AddAssert("last error recorded failure", () => jukebox.LastError.Value != null && jukebox.LastError.Value.Contains("Failing"));
        }

        private string makeOsz(string name, string audioFileName)
        {
            string dir = Path.Combine(tmp, "build_" + name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "diff.osu"),
                $"osu file format v14\n\n[General]\nAudioFilename: {audioFileName}\nMode: 0\n");
            writeSilentWav(Path.Combine(dir, audioFileName), 1);
            string osz = Path.Combine(tmp, name + ".osz");
            ZipFile.CreateFromDirectory(dir, osz);
            return osz;
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

        // Serves distinct fixture .osz files per setId; download for an unregistered id throws,
        // simulating a mirror/extract failure for the "failing set" case. SearchAsync always
        // returns empty (radio isn't exercised by this test's happy paths).
        private class FixtureMirror : IBeatmapMirror
        {
            private readonly Dictionary<int, string> paths = new();
            public string Name => "fixture";

            public void Register(int setId, string oszPath) => paths[setId] = oszPath;

            public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest r, CancellationToken ct = default)
                => Task.FromResult(new List<BeatmapSetInfo>());

            public async Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default)
            {
                if (!paths.TryGetValue(setId, out string? path))
                    throw new IOException($"fixture mirror has no set {setId}");

                using var fs = File.OpenRead(path);
                await fs.CopyToAsync(destination, ct);
            }
        }
    }
}
