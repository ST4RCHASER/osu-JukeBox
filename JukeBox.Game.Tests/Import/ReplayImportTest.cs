#nullable enable

using System.IO;
using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Import;
using JukeBox.Game.Online;
using JukeBox.Game.Replays;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Import
{
    /// <summary>
    /// The replay pieces that need no framework host: header parsing, full decode against the
    /// matched difficulty, the replay registry's per-difficulty keying, and the shape of the
    /// checksum search issued to the mirror.
    /// </summary>
    [TestFixture]
    public class ReplayImportTest
    {
        private string tmp = null!;
        private string beatmapPath = null!;

        private const string osu_content = "osu file format v14\n\n"
                                           + "[General]\nAudioFilename: audio.mp3\nMode: 0\n\n"
                                           + "[Metadata]\nTitle:Replayed Song\nArtist:Some Artist\nCreator:Some Mapper\nVersion:Hard\nBeatmapSetID:777333\n\n"
                                           + "[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:8\nApproachRate:9\nSliderMultiplier:1.4\nSliderTickRate:1\n\n"
                                           + "[TimingPoints]\n0,500,4,2,0,60,1,0\n\n"
                                           + "[HitObjects]\n256,192,1000,1,0,0:0:0:0:\n128,96,1500,1,0,0:0:0:0:\n320,240,2000,1,0,0:0:0:0:\n";

        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            beatmapPath = Path.Combine(tmp, "map [Hard].osu");
            File.WriteAllText(beatmapPath, osu_content);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, true);
        }

        private string writeReplay(string player = "Cookiezi")
        {
            string path = Path.Combine(tmp, "replay.osr");
            ReplayFixture.Write(path, beatmapPath, player);
            return path;
        }

        [Test]
        public void HeaderCarriesThePlayerAndTheBeatmapChecksum()
        {
            var header = OsrReader.ReadHeader(writeReplay());

            Assert.That(header.PlayerName, Is.EqualTo("Cookiezi"));
            Assert.That(header.BeatmapMd5, Is.EqualTo(ReplayFixture.Md5OfFile(beatmapPath)));
            Assert.That(header.RulesetId, Is.EqualTo(0));
        }

        // The header has to be readable BEFORE the beatmap is known — that ordering is the whole
        // reason it isn't lazer's decoder doing this first step.
        [Test]
        public void HeaderIsReadableWithNoBeatmapAvailableAtAll()
        {
            string replay = writeReplay("Mrekk");
            File.Delete(beatmapPath);

            Assert.That(OsrReader.ReadHeader(replay).PlayerName, Is.EqualTo("Mrekk"));
        }

        [Test]
        public void DecodingAgainstTheMatchedDifficultyYieldsReplayFrames()
        {
            var score = new JukeBoxScoreDecoder(beatmapPath).Decode(writeReplay("Rafis"));

            Assert.That(score.Replay, Is.Not.Null);
            Assert.That(score.Replay.Frames, Is.Not.Empty, "the real play's frames must survive the round trip");
            Assert.That(score.ScoreInfo.User.Username, Is.EqualTo("Rafis"));
            Assert.That(score.ScoreInfo.Ruleset.ShortName, Is.EqualTo("osu"));

            // Frames are absolute-time cursor positions; the first must land at the play's start
            // rather than at some conversion-mangled offset.
            Assert.That(score.Replay.Frames[0].Time, Is.EqualTo(0).Within(1));
        }

        [TestCase("not a replay at all, just text")]
        [TestCase("")]
        public void AFileThatIsNotAReplayIsRejectedCleanly(string content)
        {
            string path = Path.Combine(tmp, "bogus.osr");
            File.WriteAllText(path, content);

            Assert.That(() => OsrReader.ReadHeader(path), Throws.InstanceOf<InvalidDataException>());
        }

        [Test]
        public void TheDifficultyPlayedIsFoundByHashingTheCachedFiles()
        {
            string other = Path.Combine(tmp, "map [Easy].osu");
            File.WriteAllText(other, osu_content.Replace("Version:Hard", "Version:Easy"));

            var cached = new CachedBeatmapSet
            {
                SetId = 777333,
                Directory = tmp,
                OsuFiles = { other, beatmapPath },
                PreferredOsuFile = other,
            };

            string resolved = DroppedFileImporter.ResolveDifficulty(cached, new BeatmapSetInfo { Id = 777333 }, ReplayFixture.Md5OfFile(beatmapPath))!;

            Assert.That(resolved, Is.EqualTo(beatmapPath));
        }

        // The case that actually bites in production: mirrors repack archives (NeriNyan rewrites
        // .osu files when serving a no-video download), so the cached bytes hash to something the
        // replay has never heard of — measured against a real set, EVERY cached difficulty hashed
        // differently from the checksums osu! publishes. The mirror's own per-difficulty checksums
        // still name the right one.
        [Test]
        public void ARepackedArchiveStillResolvesTheDifficultyViaThePublishedChecksums()
        {
            var cached = new CachedBeatmapSet
            {
                SetId = 777333,
                Directory = tmp,
                OsuFiles = { beatmapPath },
                PreferredOsuFile = beatmapPath,
                Difficulties = { new DifficultyInfo { Path = beatmapPath, Version = "Hard", Mode = 0 } },
            };

            // Nothing in the cache hashes to this — it is the CANONICAL checksum, which the mirror
            // publishes for the difficulty named "Hard".
            const string canonical = "5a79fbf5ade4343aefa09991d6af0dc4";

            var online = new BeatmapSetInfo
            {
                Id = 777333,
                Beatmaps = { new BeatmapInfo { Version = "Hard", Mode = "osu", Checksum = canonical } },
            };

            Assert.That(DroppedFileImporter.ResolveDifficulty(cached, online, canonical), Is.EqualTo(beatmapPath));
        }

        [Test]
        public void AnUnmatchableChecksumResolvesToNoDifficulty()
        {
            var cached = new CachedBeatmapSet
            {
                SetId = 777333,
                Directory = tmp,
                OsuFiles = { beatmapPath },
                Difficulties = { new DifficultyInfo { Path = beatmapPath, Version = "Hard", Mode = 0 } },
            };

            Assert.That(DroppedFileImporter.ResolveDifficulty(cached, new BeatmapSetInfo { Id = 777333 }, "0000000000000000000000000000dead"), Is.Null,
                "an unmatched difficulty must fall back to autoplay on the set default, not guess");
        }

        [Test]
        public void PublishedChecksumsAreParsedFromMirrorResponses()
        {
            var sets = BeatmapSetInfo.ParseList("""
                [{"id":1,"beatmaps":[{"id":2,"mode":"osu","version":"Hard","checksum":"5a79fbf5ade4343aefa09991d6af0dc4"}]}]
                """);

            Assert.That(sets[0].Beatmaps[0].Checksum, Is.EqualTo("5a79fbf5ade4343aefa09991d6af0dc4"));
        }

        [Test]
        public void TheRegistryKeysReplaysByTheExactDifficultyPlayed()
        {
            var store = new ReplayStore();
            var attachment = new ReplayAttachment { PlayerName = "WhiteCat", OsuFile = beatmapPath };

            store.Register(attachment);

            Assert.That(store.ForOsuFile(beatmapPath), Is.SameAs(attachment));
            Assert.That(store.ForOsuFile(Path.Combine(tmp, "other [Easy].osu")), Is.Null,
                "another difficulty of the same set must fall back to autoplay");
            Assert.That(store.ForOsuFile(null), Is.Null);
        }

        [Test]
        public void AnUnresolvedDifficultyRegistersNothing()
        {
            var store = new ReplayStore();
            store.Register(new ReplayAttachment { PlayerName = "Nobody", OsuFile = null });

            Assert.That(store.ForOsuFile(beatmapPath), Is.Null);
        }

        // The checksum lookup is a field-restricted legacy search — the same mechanism the map-ID
        // lookup uses with option=setId — so the request has to actually carry option=checksum.
        [Test]
        public void ChecksumSearchIsIssuedAsAFieldRestrictedLegacyQuery()
        {
            string md5 = ReplayFixture.Md5OfFile(beatmapPath);
            string url = NerinyanMirror.BuildSearchUrl(new SearchRequest { Query = md5, Option = "checksum", Status = "ranked", PageSize = 5 });

            Assert.That(url, Does.Contain("option=checksum"));
            Assert.That(url, Does.Contain($"q={md5}"));
            Assert.That(url, Does.Contain("ps=5"));
        }

        [Test]
        public void ADroppedReplayTravelsWithTheSetItResolvedTo()
        {
            var attachment = new ReplayAttachment { PlayerName = "Vaxei", OsuFile = beatmapPath };
            var set = new BeatmapSetInfo { Id = 777333, Title = "Replayed Song", Replay = attachment };

            // What the queue card and playback panel read.
            Assert.That(set.Replay?.PlayerName, Is.EqualTo("Vaxei"));

            // And what never leaves the app: no mirror has a field for it.
            Assert.That(System.Text.Json.JsonSerializer.Serialize(set), Does.Not.Contain("Vaxei"));
        }

        [Test]
        public void SetsWithNoReplayAreUnaffected()
            => Assert.That(new BeatmapSetInfo { Id = 1 }.Replay, Is.Null);

        [Test]
        public void EveryRulesetIdResolvesToItsOwnRulesetForDecoding()
        {
            Assert.That(JukeBox.Game.LazerPlayer.LazerChartLayer.CreateRuleset(0).ShortName, Is.EqualTo("osu"));
            Assert.That(JukeBox.Game.LazerPlayer.LazerChartLayer.CreateRuleset(1).ShortName, Is.EqualTo("taiko"));
            Assert.That(JukeBox.Game.LazerPlayer.LazerChartLayer.CreateRuleset(2).ShortName, Is.EqualTo("fruits"));
            Assert.That(JukeBox.Game.LazerPlayer.LazerChartLayer.CreateRuleset(3).ShortName, Is.EqualTo("mania"));
        }
    }
}
