using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Online;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Beatmaps
{
    [TestFixture]
    public class BeatmapCacheTest
    {
        private string tmp = null!;

        private const string osu_content = "osu file format v14\n\n[General]\nAudioFilename: audio.mp3\nMode: 0\nWidescreenStoryboard: 1\n\n[Events]\n//Background and Video events\n0,0,\"bg.jpg\",0,0\nVideo,100,\"movie.mp4\"\n";

        [SetUp]
        public void SetUp() => tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        [TearDown]
        public void TearDown() { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }

        private string makeOsz()
        {
            string dir = Path.Combine(tmp, "build"); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "test.osu"), osu_content);
            File.WriteAllText(Path.Combine(dir, "sb.osb"), "[Events]\n");
            File.WriteAllBytes(Path.Combine(dir, "audio.mp3"), new byte[] { 0xFF });
            File.WriteAllBytes(Path.Combine(dir, "bg.jpg"), new byte[] { 0xFF });
            string osz = Path.Combine(tmp, "fixture.osz");
            ZipFile.CreateFromDirectory(dir, osz);
            return osz;
        }

        [Test]
        public void ScannerReadsGeneralAndEvents()
        {
            Directory.CreateDirectory(tmp);
            string osu = Path.Combine(tmp, "a.osu");
            File.WriteAllText(osu, osu_content);
            var info = OsuFileScanner.Scan(osu);
            Assert.That(info.AudioFilename, Is.EqualTo("audio.mp3"));
            Assert.That(info.Mode, Is.EqualTo(0));
            Assert.That(info.Widescreen, Is.True);
            Assert.That(info.BackgroundFilename, Is.EqualTo("bg.jpg"));
            Assert.That(info.VideoFilename, Is.EqualTo("movie.mp4"));
        }

        [Test]
        public async Task DownloadsExtractsAndScans()
        {
            string osz = makeOsz();
            var mirror = new FileMirror(osz);   // serves the osz bytes as DownloadAsync
            var cache = new BeatmapCache(Path.Combine(tmp, "cache"), mirror);
            var set = await cache.GetAsync(123);
            Assert.That(cache.IsCached(123), Is.True);
            Assert.That(File.Exists(set.AudioFile), Is.True);
            Assert.That(set.OsbFile, Does.EndWith("sb.osb"));
            Assert.That(set.PreferredOsuFile, Does.EndWith("test.osu"));
            Assert.That(set.Widescreen, Is.True);
            Assert.That(set.VideoFile, Is.Null); // movie.mp4 not present in zip → null
        }

        [Test]
        public void IsCachedRecognizesNestedOsuFile()
        {
            string cacheDir = Path.Combine(tmp, "cache");
            string setDir = Path.Combine(cacheDir, "456", "sub");
            Directory.CreateDirectory(setDir);
            File.WriteAllText(Path.Combine(setDir, "nested.osu"), osu_content);

            var cache = new BeatmapCache(cacheDir, new FileMirror(Path.Combine(tmp, "unused.osz")));
            Assert.That(cache.IsCached(456), Is.True);
        }
    }

    public class FileMirror : IBeatmapMirror
    {
        private readonly string path;
        public FileMirror(string path) => this.path = path;
        public string Name => "file";
        public System.Threading.Tasks.Task<System.Collections.Generic.List<BeatmapSetInfo>> SearchAsync(SearchRequest r, System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult(new System.Collections.Generic.List<BeatmapSetInfo>());
        public async System.Threading.Tasks.Task DownloadAsync(int setId, bool noVideo, Stream destination, System.Threading.CancellationToken ct = default)
        {
            using var fs = File.OpenRead(path);
            await fs.CopyToAsync(destination, ct);
        }
    }
}
