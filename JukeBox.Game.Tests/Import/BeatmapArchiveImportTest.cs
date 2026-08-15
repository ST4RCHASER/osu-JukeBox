#nullable enable

using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Import;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Import
{
    /// <summary>
    /// Local .osz import: <see cref="BeatmapCache.ImportArchive"/> lands a dropped archive in the
    /// cache under an id taken from its own contents, and everything downstream (cache hits,
    /// eviction, the queue metadata) tolerates the synthetic negative id used when the archive
    /// declares none.
    /// </summary>
    [TestFixture]
    public class BeatmapArchiveImportTest
    {
        private string tmp = null!;

        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, true);
        }

        private static string osuContent(int? setId, string title = "Some Song", string artist = "Some Artist", string creator = "Some Mapper", string version = "Insane")
        {
            string setIdLine = setId == null ? "" : $"BeatmapSetID:{setId}\n";

            return "osu file format v14\n\n"
                   + "[General]\nAudioFilename: audio.mp3\nMode: 0\n\n"
                   + $"[Metadata]\nTitle:{title}\nTitleUnicode:{title}\nArtist:{artist}\nArtistUnicode:{artist}\nCreator:{creator}\nVersion:{version}\n{setIdLine}\n"
                   + "[Events]\n0,0,\"bg.jpg\",0,0\n";
        }

        private string makeOsz(string name, int? setId, string title = "Some Song", string artist = "Some Artist", string creator = "Some Mapper")
        {
            string dir = Path.Combine(tmp, "build-" + name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "map [Insane].osu"), osuContent(setId, title, artist, creator));
            File.WriteAllBytes(Path.Combine(dir, "audio.mp3"), new byte[] { 0xFF });
            File.WriteAllBytes(Path.Combine(dir, "bg.jpg"), new byte[] { 0xFF });

            string osz = Path.Combine(tmp, name + ".osz");
            ZipFile.CreateFromDirectory(dir, osz);
            return osz;
        }

        private BeatmapCache newCache() => new BeatmapCache(Path.Combine(tmp, "cache"), new ThrowingMirror());

        [Test]
        public void DeclaredSetIdIsUsedAsTheCacheId()
        {
            var cache = newCache();
            var set = cache.ImportArchive(makeOsz("declared", setId: 998877));

            Assert.That(set.SetId, Is.EqualTo(998877));
            Assert.That(cache.IsCached(998877), Is.True);
            Assert.That(Path.GetFileName(set.Directory), Is.EqualTo("998877"));
            Assert.That(set.PreferredOsuFile, Is.Not.Null);
            Assert.That(File.Exists(set.AudioFile), Is.True);
        }

        [Test]
        public void ArchiveWithoutASetIdGetsAStableNegativeLocalId()
        {
            var cache = newCache();

            int first = cache.ImportArchive(makeOsz("unsubmitted", setId: null)).SetId;
            int again = cache.ImportArchive(makeOsz("unsubmitted-copy", setId: null)).SetId;

            Assert.That(first, Is.Negative, "local ids must never collide with real (positive) beatmapset ids");
            Assert.That(again, Is.EqualTo(first), "same metadata must resolve to the same cache directory");
            Assert.That(cache.IsCached(first), Is.True);
        }

        [Test]
        public void ArchiveWithTheSentinelSetIdAlsoFallsBackToALocalId()
        {
            var cache = newCache();
            Assert.That(cache.ImportArchive(makeOsz("sentinel", setId: -1)).SetId, Is.Negative);
        }

        [Test]
        public void DifferentUnsubmittedSetsGetDifferentLocalIds()
        {
            var cache = newCache();

            int a = cache.ImportArchive(makeOsz("a", null, title: "Song A")).SetId;
            int b = cache.ImportArchive(makeOsz("b", null, title: "Song B")).SetId;

            Assert.That(a, Is.Not.EqualTo(b));
        }

        [Test]
        public async Task ALocallyImportedSetIsACacheHitAndNeverTouchesTheMirror()
        {
            var cache = newCache(); // ThrowingMirror: any download attempt fails the test
            int id = cache.ImportArchive(makeOsz("hit", setId: null)).SetId;

            var reloaded = await cache.GetAsync(id);

            Assert.That(reloaded.SetId, Is.EqualTo(id));
            Assert.That(reloaded.OsuFiles, Is.Not.Empty);
        }

        [Test]
        public void LocallyImportedSetsAreNeverEvicted()
        {
            var cache = newCache();
            int id = cache.ImportArchive(makeOsz("protected", setId: null)).SetId;

            // A limit of zero would evict everything evictable.
            cache.EvictToLimit(0, System.Array.Empty<int>());

            Assert.That(cache.IsCached(id), Is.True, "a dropped .osz has no mirror to re-download it from");
        }

        [Test]
        public void ReimportingReplacesTheExistingDirectory()
        {
            var cache = newCache();
            var first = cache.ImportArchive(makeOsz("replace", setId: 4242));

            // Leave a stray file behind; a re-import must not merge with it.
            File.WriteAllText(Path.Combine(first.Directory, "stray.txt"), "x");

            var second = cache.ImportArchive(makeOsz("replace-again", setId: 4242));

            Assert.That(second.SetId, Is.EqualTo(4242));
            Assert.That(File.Exists(Path.Combine(second.Directory, "stray.txt")), Is.False);
        }

        [Test]
        public void AnArchiveWithNoDifficultyIsRejectedAndLeavesNothingBehind()
        {
            string dir = Path.Combine(tmp, "build-empty");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "audio.mp3"), new byte[] { 0xFF });
            string osz = Path.Combine(tmp, "empty.osz");
            ZipFile.CreateFromDirectory(dir, osz);

            var cache = newCache();

            Assert.That(() => cache.ImportArchive(osz), Throws.InstanceOf<InvalidDataException>());
            Assert.That(Directory.EnumerateDirectories(Path.Combine(tmp, "cache")), Is.Empty);
        }

        [Test]
        public void MetadataForTheQueueComesFromTheArchivesOwnOsuFile()
        {
            var cache = newCache();
            var cached = cache.ImportArchive(makeOsz("meta", setId: null, title: "Blue Zenith", artist: "xi", creator: "Asphyxia"));

            var described = LocalBeatmapMetadata.Describe(cached);

            Assert.That(described.Id, Is.EqualTo(cached.SetId));
            Assert.That(described.DisplayTitle, Is.EqualTo("Blue Zenith"));
            Assert.That(described.DisplayArtist, Is.EqualTo("xi"));
            Assert.That(described.Creator, Is.EqualTo("Asphyxia"));
            Assert.That(described.Beatmaps.Single().Mode, Is.EqualTo("osu"));
            Assert.That(described.Beatmaps.Single().Version, Is.EqualTo("Insane"));
        }

        [Test]
        public void ScannerReadsTheMetadataSection()
        {
            string osu = Path.Combine(tmp, "meta.osu");
            File.WriteAllText(osu, osuContent(123456, "Title", "Artist", "Mapper", "Extra"));

            var info = OsuFileScanner.Scan(osu);

            Assert.That(info.BeatmapSetId, Is.EqualTo(123456));
            Assert.That(info.Title, Is.EqualTo("Title"));
            Assert.That(info.Artist, Is.EqualTo("Artist"));
            Assert.That(info.Creator, Is.EqualTo("Mapper"));
            Assert.That(info.Version, Is.EqualTo("Extra"));
        }

        [Test]
        public void ScannerReportsNoSetIdWhenTheFileDeclaresNone()
            => Assert.That(OsuFileScanner.ScanLines(osuContent(null).Split('\n')).BeatmapSetId, Is.LessThanOrEqualTo(0));

        /// <summary>Fails the test if anything tries to reach a mirror — a locally-imported set
        /// must be servable entirely from disk.</summary>
        private class ThrowingMirror : Game.Online.IBeatmapMirror
        {
            public string Name => "throwing";

            public Task<System.Collections.Generic.List<Game.Online.BeatmapSetInfo>> SearchAsync(Game.Online.SearchRequest request, System.Threading.CancellationToken ct = default)
                => throw new System.InvalidOperationException("no mirror search expected");

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, System.Threading.CancellationToken ct = default, Game.Online.DownloadProgressCallback? progress = null)
                => throw new System.InvalidOperationException($"no mirror download expected (set {setId})");
        }
    }
}
