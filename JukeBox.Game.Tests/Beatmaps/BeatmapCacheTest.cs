using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

        // One caller giving up must not take the download away from the others. This is the case
        // that made per-caller tokens necessary: an advance abandoning a set it no longer needs
        // used to cancel the SHARED task, so a prefetch of that same set for something still queued
        // died with it — the first caller in owned everyone's cancellation.
        [Test]
        public async Task OneCallerCancellingDoesNotTakeTheDownloadFromAnother()
        {
            var mirror = new GatedMirror(makeOsz());
            var cache = new BeatmapCache(Path.Combine(tmp, "cache"), mirror);

            using var giveUp = new System.Threading.CancellationTokenSource();

            var abandons = cache.GetAsync(42, giveUp.Token);
            var persists = cache.GetAsync(42);

            await waitUntil(() => mirror.Started > 0, "the download to start");

            giveUp.Cancel();

            Assert.That(async () => await abandons, Throws.InstanceOf<OperationCanceledException>(),
                "the caller that gave up sees its own cancellation");
            Assert.That(mirror.Cancelled, Is.Zero, "but the request itself keeps running for the other caller");

            mirror.Release();

            var set = await persists;

            Assert.That(set.SetId, Is.EqualTo(42));
            Assert.That(cache.IsCached(42), Is.True);
            Assert.That(mirror.Started, Is.EqualTo(1), "and they shared one download rather than racing two");
        }

        // …and when the LAST caller lets go, the request really is aborted rather than left running
        // detached, burning bandwidth the current song needs.
        [Test]
        public async Task TheLastCallerLettingGoActuallyAbortsTheRequest()
        {
            var mirror = new GatedMirror(makeOsz());
            var cache = new BeatmapCache(Path.Combine(tmp, "cache"), mirror);

            using var giveUp = new System.Threading.CancellationTokenSource();

            var only = cache.GetAsync(42, giveUp.Token);
            await waitUntil(() => mirror.Started > 0, "the download to start");

            giveUp.Cancel();

            Assert.That(async () => await only, Throws.InstanceOf<OperationCanceledException>());
            await waitUntil(() => mirror.Cancelled > 0, "the mirror request to be cancelled");

            Assert.That(cache.IsCached(42), Is.False);
            Assert.That(cache.IsDownloading(42), Is.False, "and nothing is left claiming to be in flight");
        }

        // An abandoned download must leave nothing behind that a later attempt trips over — neither
        // a sticky in-flight entry nor a partial file that looks like progress.
        [Test]
        public async Task AnAbandonedSetDownloadsCleanlyOnALaterRequest()
        {
            string osz = makeOsz();
            var mirror = new GatedMirror(osz);
            var cache = new BeatmapCache(Path.Combine(tmp, "cache"), mirror);

            using (var giveUp = new System.Threading.CancellationTokenSource())
            {
                var abandoned = cache.GetAsync(42, giveUp.Token);
                await waitUntil(() => mirror.Started > 0, "the first download to start");
                giveUp.Cancel();
                Assert.That(async () => await abandoned, Throws.InstanceOf<OperationCanceledException>());
                await waitUntil(() => mirror.Cancelled > 0, "the first attempt to abort");
            }

            Assert.That(Directory.GetFiles(Path.Combine(tmp, "cache"), "*.osz.part"), Is.Empty,
                "no half-written archive survives the abort");
            Assert.That(Directory.GetDirectories(Path.Combine(tmp, "cache"), "*.extracting"), Is.Empty,
                "nor a half-populated extract directory");

            // Same cache, same set, no leftover state: it simply downloads.
            var retry = new GatedMirror(osz);
            var retried = new BeatmapCache(Path.Combine(tmp, "cache"), retry);
            retry.Release();

            var set = await retried.GetAsync(42);

            Assert.That(set.SetId, Is.EqualTo(42));
            Assert.That(retried.IsCached(42), Is.True);
        }

        private static async Task waitUntil(Func<bool> condition, string what)
        {
            for (int i = 0; i < 200 && !condition(); i++)
                await Task.Delay(25);

            Assert.That(condition(), Is.True, $"timed out waiting for {what}");
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

        [Test]
        public void ClearDeletesEveryRedownloadableSetAndReportsWhatItFreed()
        {
            string cacheDir = Path.Combine(tmp, "cache");
            Directory.CreateDirectory(cacheDir);

            string a = makeFakeSetDir(cacheDir, 100, 1024 * 1024, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            string b = makeFakeSetDir(cacheDir, 200, 2 * 1024 * 1024, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

            var cache = new BeatmapCache(cacheDir, new FileMirror(Path.Combine(tmp, "unused.osz")));

            var result = cache.Clear(Array.Empty<int>());

            Assert.That(Directory.Exists(a), Is.False);
            Assert.That(Directory.Exists(b), Is.False);
            Assert.That(result.SetsDeleted, Is.EqualTo(2));
            Assert.That(result.BytesFreed, Is.EqualTo(3 * 1024 * 1024));
            Assert.That(result.SetsKeptInUse, Is.Zero);
            Assert.That(result.SetsKeptLocal, Is.Zero);
        }

        // The set that is playing keeps its folder: the storyboard and video read from it for the
        // rest of the song, so deleting it would break what is on screen right now.
        [Test]
        public void ClearKeepsTheSetThatIsPlayingAndSaysSo()
        {
            string cacheDir = Path.Combine(tmp, "cache");
            Directory.CreateDirectory(cacheDir);

            string playing = makeFakeSetDir(cacheDir, 100, 1024 * 1024, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            string other = makeFakeSetDir(cacheDir, 200, 1024 * 1024, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

            var cache = new BeatmapCache(cacheDir, new FileMirror(Path.Combine(tmp, "unused.osz")));

            var result = cache.Clear(new[] { 100 });

            Assert.That(Directory.Exists(playing), Is.True);
            Assert.That(Directory.Exists(other), Is.False);
            Assert.That(result.SetsDeleted, Is.EqualTo(1));
            Assert.That(result.SetsKeptInUse, Is.EqualTo(1));
            Assert.That(result.BytesFreed, Is.EqualTo(1024 * 1024), "the kept set's bytes are not counted as freed");
        }

        // Negative ids are sets the user dragged in as a .osz. No mirror has them, so deleting one
        // destroys the only copy — which is not what "clear the cache" is asking for.
        [Test]
        public void ClearNeverDeletesLocallyImportedSets()
        {
            string cacheDir = Path.Combine(tmp, "cache");
            Directory.CreateDirectory(cacheDir);

            string local = makeFakeSetDir(cacheDir, -42, 1024 * 1024, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            string downloaded = makeFakeSetDir(cacheDir, 200, 1024 * 1024, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

            var cache = new BeatmapCache(cacheDir, new FileMirror(Path.Combine(tmp, "unused.osz")));

            var result = cache.Clear(Array.Empty<int>());

            Assert.That(Directory.Exists(local), Is.True);
            Assert.That(Directory.Exists(downloaded), Is.False);
            Assert.That(result.SetsKeptLocal, Is.EqualTo(1));
        }

        // A download in flight extracts into "<id>.extracting"; deleting that mid-import would
        // break it. Only bare-integer directories are sets.
        [Test]
        public void ClearLeavesAnInFlightImportAlone()
        {
            string cacheDir = Path.Combine(tmp, "cache");
            string staging = Path.Combine(cacheDir, "777.extracting");
            Directory.CreateDirectory(staging);
            File.WriteAllBytes(Path.Combine(staging, "partial.bin"), new byte[1024]);

            var cache = new BeatmapCache(cacheDir, new FileMirror(Path.Combine(tmp, "unused.osz")));

            cache.Clear(Array.Empty<int>());

            Assert.That(Directory.Exists(staging), Is.True);
        }

        /// <summary>
        /// The claim that makes clearing safe for queued songs: a set that was cleared is simply
        /// fetched again the next time it is asked for, rather than erroring. Exercised at the
        /// cache itself, which is where the queue's play path lands (<c>Jukebox</c> checks
        /// <see cref="BeatmapCache.IsCached"/> and falls through to <see cref="BeatmapCache.GetAsync"/>).
        /// </summary>
        [Test]
        public async Task AClearedSetIsDownloadedAgainOnDemand()
        {
            var mirror = new FileMirror(makeOsz());
            var cache = new BeatmapCache(Path.Combine(tmp, "cache"), mirror);

            await cache.GetAsync(4242).ConfigureAwait(false);
            Assert.That(cache.IsCached(4242), Is.True, "cached by the first fetch");

            cache.Clear(Array.Empty<int>());
            Assert.That(cache.IsCached(4242), Is.False, "and gone after the clear");

            var again = await cache.GetAsync(4242).ConfigureAwait(false);

            Assert.That(cache.IsCached(4242), Is.True, "re-downloaded on demand rather than throwing");
            Assert.That(Directory.EnumerateFiles(again.Directory, "*.osu", SearchOption.AllDirectories).Any(), Is.True);
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

    /// <summary>
    /// Serves the same bytes as <see cref="FileMirror"/> but only once released, so a test can hold
    /// a download open and act while it is in flight. Records what actually reached the mirror —
    /// how many downloads were started and how many were cancelled — because "the caller stopped
    /// waiting" and "the request was actually aborted" are different claims.
    /// </summary>
    public class GatedMirror : IBeatmapMirror
    {
        private readonly string path;
        private readonly TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedMirror(string path) => this.path = path;

        public string Name => "gated";

        public int Started;
        public int Cancelled;

        public void Release() => gate.TrySetResult(true);

        public Task<System.Collections.Generic.List<BeatmapSetInfo>> SearchAsync(SearchRequest r, System.Threading.CancellationToken ct = default)
            => Task.FromResult(new System.Collections.Generic.List<BeatmapSetInfo>());

        public async Task DownloadAsync(int setId, bool noVideo, Stream destination, System.Threading.CancellationToken ct = default, DownloadProgressCallback progress = null)
        {
            System.Threading.Interlocked.Increment(ref Started);

            try
            {
                await gate.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                System.Threading.Interlocked.Increment(ref Cancelled);
                throw;
            }

            using var fs = File.OpenRead(path);
            await fs.CopyToAsync(destination, ct);
        }
    }

    public class FileMirror : IBeatmapMirror
    {
        private readonly string path;
        public FileMirror(string path) => this.path = path;
        public string Name => "file";
        public System.Threading.Tasks.Task<System.Collections.Generic.List<BeatmapSetInfo>> SearchAsync(SearchRequest r, System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult(new System.Collections.Generic.List<BeatmapSetInfo>());
        public async System.Threading.Tasks.Task DownloadAsync(int setId, bool noVideo, Stream destination, System.Threading.CancellationToken ct = default, DownloadProgressCallback progress = null)
        {
            using var fs = File.OpenRead(path);
            await fs.CopyToAsync(destination, ct);
        }
    }
}
