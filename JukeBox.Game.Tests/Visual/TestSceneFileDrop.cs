#nullable enable

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Import;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// End-to-end drag-and-drop: drives <see cref="DroppedFileImporter.Import"/> with real file
    /// paths — the same entry point the window's <c>DragDrop</c> event calls — and asserts what the
    /// user would see (queue contents, playback, the toast text carried by
    /// <see cref="DroppedFileImporter.Notification"/>).
    ///
    /// <para>
    /// Lives under Visual/ rather than as a pure NUnit fixture for the same reason
    /// <see cref="TestSceneJukebox"/> does: the importer is a <c>Component</c> and its enqueue path
    /// needs a real <see cref="Jukebox"/> + <see cref="PlaybackController"/>, which need framework
    /// context (AudioManager, GameHost) to load a track.
    /// </para>
    /// </summary>
    [TestFixture]
    public partial class TestSceneFileDrop : JukeBoxTestScene
    {
        private string tmp = null!;

        private MusicQueue queue = null!;
        private BeatmapCache cache = null!;
        private PlaybackController playback = null!;
        private Jukebox jukebox = null!;
        private DroppedFileImporter importer = null!;

        // CreateChildDependencies runs once for the whole fixture, so everything [Resolved] hands
        // out is created once here and reset (never recreated) between tests — see the same note
        // on TestSceneNowPlayingPanel.
        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            queue = new MusicQueue();
            cache = new BeatmapCache(Path.Combine(tmp, "cache"), new EmptyMirror());
            playback = new PlaybackController();
            jukebox = new Jukebox(queue, new RadioService(new EmptyMirror()), cache, playback);

            var dependencies = new DependencyContainer(base.CreateChildDependencies(parent));
            dependencies.CacheAs(queue);
            dependencies.CacheAs(cache);
            dependencies.CacheAs(playback);
            dependencies.CacheAs(jukebox);
            return dependencies;
        }

        // NOTE: deliberately no [TearDown] delete of `tmp` — TestScene runs queued step bodies from
        // a base-class teardown hook that fires after a derived [TearDown], so deleting the fixture
        // files here would race still-pending steps (same reasoning as TestSceneJukebox).

        private Container importerHost = null!;

        // playback/jukebox are added exactly once, for the fixture's whole lifetime: SetUpSteps'
        // per-test child reassignment disposes whatever it held, and disposing the very instances
        // CreateChildDependencies cached would break every test after the first (see
        // TestSceneNowPlayingPanel's identical note).
        protected override void LoadComplete()
        {
            base.LoadComplete();
            Add(playback);
            Add(jukebox);
            Add(importerHost = new Container { RelativeSizeAxes = Axes.Both });
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("reset playback and queue", () =>
            {
                // Back to the idle state the jukebox treats as "nothing has played yet", so each
                // test's own drop is what starts playback rather than the previous test's track
                // still holding the slot.
                playback.Stop();
                playback.Current.Value = null;
                queue.Items.Clear();
            });

            AddStep("create importer", () => importerHost.Child = importer = new DroppedFileImporter());
        }

        private string lastMessage => importer.Notification.Value?.Message ?? string.Empty;

        [Test]
        public void DroppingAnOszCachesQueuesAndPlaysIt()
        {
            string osz = null!;

            AddStep("build a .osz fixture", () => osz = makeOsz("dropped", setId: 555111, title: "Dropped Song"));
            AddStep("drop it", () => importer.Import(osz));

            AddUntilStep("toast reports the imported title", () => lastMessage == "Added: Dropped Song");
            AddAssert("extracted into the cache under its declared id", () => cache.IsCached(555111));
            AddUntilStep("it is what's now playing", () => playback.Current.Value?.SetId == 555111);
        }

        [Test]
        public void DroppingAnOszWhileSomethingPlaysLeavesItQueued()
        {
            string first = null!;
            string second = null!;

            AddStep("build two .osz fixtures", () =>
            {
                first = makeOsz("first", setId: 555222, title: "First");
                second = makeOsz("second", setId: 555333, title: "Second");
            });

            AddStep("drop the first", () => importer.Import(first));
            AddUntilStep("first is playing", () => playback.Current.Value?.SetId == 555222);

            AddStep("drop the second", () => importer.Import(second));
            AddUntilStep("second reported", () => lastMessage == "Added: Second");
            AddAssert("second waits in the queue behind the first", () => queue.Items.Any(i => i.Id == 555333));
        }

        [Test]
        public void DroppingAnOszWithoutASetIdStillPlays()
        {
            string osz = null!;

            AddStep("build an unsubmitted .osz fixture", () => osz = makeOsz("unsubmitted", setId: null, title: "Unsubmitted"));
            AddStep("drop it", () => importer.Import(osz));

            AddUntilStep("toast reports the imported title", () => lastMessage == "Added: Unsubmitted");
            AddUntilStep("plays under a synthetic local (negative) id", () => playback.Current.Value?.SetId < 0);
        }

        [Test]
        public void DroppingAnUnsupportedFileReportsItRatherThanFailingSilently()
        {
            string other = null!;

            AddStep("write a non-osu file", () =>
            {
                other = Path.Combine(tmp, "notes.txt");
                File.WriteAllText(other, "hello");
            });

            AddStep("drop it", () => importer.Import(other));
            AddUntilStep("reported as unsupported", () => lastMessage.Contains("notes.txt") && lastMessage.Contains(".osz"));
            AddAssert("flagged as an error", () => importer.Notification.Value?.IsError == true);
        }

        [Test]
        public void DroppingABrokenArchiveReportsTheFailure()
        {
            string broken = null!;

            AddStep("write a .osz that isn't a zip", () =>
            {
                broken = Path.Combine(tmp, "broken.osz");
                File.WriteAllText(broken, "not a zip");
            });

            AddStep("drop it", () => importer.Import(broken));
            AddUntilStep("failure reported", () => lastMessage.StartsWith("Import failed:"));
            AddAssert("nothing queued", () => queue.Items.Count == 0);
        }

        private string makeOsz(string name, int? setId, string title)
        {
            string dir = Path.Combine(tmp, "build_" + name);
            Directory.CreateDirectory(dir);

            string setIdLine = setId == null ? "" : $"BeatmapSetID:{setId}\n";

            File.WriteAllText(Path.Combine(dir, "diff.osu"),
                "osu file format v14\n\n[General]\nAudioFilename: audio.wav\nMode: 0\n\n"
                + $"[Metadata]\nTitle:{title}\nArtist:Fixture Artist\nCreator:Fixture Mapper\nVersion:Normal\n{setIdLine}");

            writeSilentWav(Path.Combine(dir, "audio.wav"), 1);

            string osz = Path.Combine(tmp, name + ".osz");
            ZipFile.CreateFromDirectory(dir, osz);
            return osz;
        }

        // See TestSceneJukebox.writeSilentWav: BASS plays WAV directly, so a 44-byte RIFF header
        // followed by silence is enough to drive real playback.
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

        /// <summary>No search results, and any download attempt fails — a dropped set must be
        /// served entirely off the local import.</summary>
        private class EmptyMirror : IBeatmapMirror
        {
            public string Name => "empty";

            public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
                => Task.FromResult(new List<BeatmapSetInfo>());

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
                => throw new IOException($"no mirror download expected (set {setId})");
        }
    }
}
