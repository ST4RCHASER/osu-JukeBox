using System;
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

        [Test]
        public void EvictToLimitDeletesOldestUntilUnderLimit()
        {
            string cacheDir = Path.Combine(tmp, "cache");
            Directory.CreateDirectory(cacheDir);

            // Three fake set dirs, ~1 MB each, staggered mtimes (100 = oldest, 300 = newest).
            string dirOld = makeFakeSetDir(cacheDir, 100, 1024 * 1024, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            string dirMid = makeFakeSetDir(cacheDir, 200, 1024 * 1024, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            string dirNew = makeFakeSetDir(cacheDir, 300, 1024 * 1024, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));

            var cache = new BeatmapCache(cacheDir, new FileMirror(Path.Combine(tmp, "unused.osz")));

            // ~3 MB total; limit of 2 MB forces deletion of the single oldest set (100).
            cache.EvictToLimit(2 * 1024 * 1024, Array.Empty<int>());

            Assert.That(Directory.Exists(dirOld), Is.False);
            Assert.That(Directory.Exists(dirMid), Is.True);
            Assert.That(Directory.Exists(dirNew), Is.True);
        }

        [Test]
        public void EvictToLimitNeverDeletesProtectedIds()
        {
            string cacheDir = Path.Combine(tmp, "cache");
            Directory.CreateDirectory(cacheDir);

            string dirOld = makeFakeSetDir(cacheDir, 100, 1024 * 1024, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            string dirMid = makeFakeSetDir(cacheDir, 200, 1024 * 1024, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            makeFakeSetDir(cacheDir, 300, 1024 * 1024, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));

            var cache = new BeatmapCache(cacheDir, new FileMirror(Path.Combine(tmp, "unused.osz")));

            // Oldest (100) is protected, so eviction must skip it even though it's the LRU pick,
            // and fall through to the next-oldest unprotected set (200) instead.
            cache.EvictToLimit(2 * 1024 * 1024, new[] { 100 });

            Assert.That(Directory.Exists(dirOld), Is.True);
            Assert.That(Directory.Exists(dirMid), Is.False);
        }

        private static string makeFakeSetDir(string cacheDir, int setId, int sizeBytes, DateTime mtimeUtc)
        {
            string dir = Path.Combine(cacheDir, setId.ToString());
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "data.bin"), new byte[sizeBytes]);
            Directory.SetLastWriteTimeUtc(dir, mtimeUtc);
            return dir;
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
