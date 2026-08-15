#nullable enable

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.Import;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using JukeBox.Game.Replays;
using JukeBox.Game.Tests.Import;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osu.Game.IO;

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
        private ReplayStore replayStore = null!;
        private FixtureMirror mirror = null!;

        // CreateChildDependencies runs once for the whole fixture, so everything [Resolved] hands
        // out is created once here and reset (never recreated) between tests — see the same note
        // on TestSceneNowPlayingPanel.
        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            queue = new MusicQueue();
            mirror = new FixtureMirror();
            cache = new BeatmapCache(Path.Combine(tmp, "cache"), mirror);
            playback = new PlaybackController();
            replayStore = new ReplayStore();

            // Radio gets its own always-empty mirror: `mirror` is scripted per-test for the
            // checksum lookup, and a radio pick landing on those results would start playing
            // something no test asked for.
            jukebox = new Jukebox(queue, new RadioService(new EmptyMirror()), cache, playback);

            var dependencies = new DependencyContainer(base.CreateChildDependencies(parent));
            dependencies.CacheAs(queue);
            dependencies.CacheAs(cache);
            dependencies.CacheAs(playback);
            dependencies.CacheAs(jukebox);
            dependencies.CacheAs(replayStore);
            dependencies.CacheAs<IBeatmapMirror>(mirror);
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
                playback.SelectedOsuFile.Value = null;
                jukebox.NowPlaying.Value = null;
                queue.Items.Clear();
                mirror.Reset();
            });

            AddStep("create importer", () => importerHost.Child = importer = new DroppedFileImporter());
        }

        private string lastMessage => importer.Notification.Value?.Message ?? string.Empty;

        // Config, the skin service and the lazer resource provider all come from the test runner
        // (JukeBoxGameBase), which caches exactly what the real game does.
        [Resolved]
        private JukeBoxConfigManager config { get; set; } = null!;

        [Resolved]
        private SkinSelection skinSelection { get; set; } = null!;

        [Resolved]
        private IStorageResourceProvider skinResources { get; set; } = null!;

        private string makeOsk(string name)
        {
            string build = Path.Combine(tmp, "build_" + name);
            Directory.CreateDirectory(build);
            File.WriteAllText(Path.Combine(build, "skin.ini"), $"[General]\nName: {name}\nVersion: 2.5\n");
            File.WriteAllBytes(Path.Combine(build, "cursor.png"), new byte[] { 0x89, 0x50 });

            string osk = Path.Combine(tmp, name + ".osk");
            ZipFile.CreateFromDirectory(build, osk);
            return osk;
        }

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
        public void DroppingAnOskImportsItSelectsItAndPersistsTheChoice()
        {
            string osk = null!;
            var originalSkin = JukeBoxSkin.Argon;
            string originalCustom = string.Empty;

            AddStep("remember the current skin settings", () =>
            {
                // This fixture shares the test runner's real config manager with every other
                // fixture in the run, so the selection is put back before leaving.
                originalSkin = config.Get<JukeBoxSkin>(JukeBoxSetting.Skin);
                originalCustom = config.Get<string>(JukeBoxSetting.CustomSkinPath);
            });

            AddStep("build a .osk fixture", () => osk = makeOsk("DropTestSkin"));
            AddStep("drop it", () => importer.Import(osk));

            AddUntilStep("toast names the imported skin", () => lastMessage == "Skin applied: DropTestSkin");
            AddAssert("Custom is the persisted choice", () => config.Get<JukeBoxSkin>(JukeBoxSetting.Skin) == JukeBoxSkin.Custom);
            AddAssert("the imported folder is the persisted path", () => config.Get<string>(JukeBoxSetting.CustomSkinPath) == "DropTestSkin");

            AddAssert("the skin service resolves it", () => skinSelection.Effective.Value == JukeBoxSkin.Custom
                                                            && skinSelection.CustomSkinDirectory != null
                                                            && File.Exists(Path.Combine(skinSelection.CustomSkinDirectory!, "skin.ini")));

            AddAssert("and builds it as the active gameplay skin", () =>
            {
                using var skin = skinSelection.CreateEffectiveSkin(skinResources);
                return skin is JukeBox.Game.LazerPlayer.ImportedLegacySkin;
            });

            AddStep("restore the previous skin settings", () =>
            {
                config.SetValue(JukeBoxSetting.CustomSkinPath, originalCustom);
                config.SetValue(JukeBoxSetting.Skin, originalSkin);
            });
        }

        // Custom is reachable from the settings dropdown whether or not anything has ever been
        // imported, so it has to degrade rather than render nothing.
        [Test]
        public void CustomSkinWithNothingImportedFallsBackToABundledSkin()
        {
            var originalSkin = JukeBoxSkin.Argon;
            string originalCustom = string.Empty;

            AddStep("select Custom with no import", () =>
            {
                originalSkin = config.Get<JukeBoxSkin>(JukeBoxSetting.Skin);
                originalCustom = config.Get<string>(JukeBoxSetting.CustomSkinPath);

                config.SetValue(JukeBoxSetting.CustomSkinPath, string.Empty);
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Custom);
            });

            AddAssert("nothing resolves", () => skinSelection.CustomSkinDirectory == null);
            AddAssert("a bundled skin is built instead", () =>
            {
                using var skin = skinSelection.CreateEffectiveSkin(skinResources);
                return skin is not JukeBox.Game.LazerPlayer.ImportedLegacySkin;
            });

            AddStep("restore the previous skin settings", () =>
            {
                config.SetValue(JukeBoxSetting.CustomSkinPath, originalCustom);
                config.SetValue(JukeBoxSetting.Skin, originalSkin);
            });
        }

        [Test]
        public void DroppingAnOsrResolvesTheBeatmapByChecksumAndQueuesItWithTheReplay()
        {
            string osr = null!;

            AddStep("publish a set the replay's checksum resolves to", () => osr = makeReplayFor("replayed", setId: 606060, player: "Cookiezi"));
            AddStep("drop the replay", () => importer.Import(osr));

            AddUntilStep("toast credits the player", () => lastMessage == "Added: Replayed Song — played by Cookiezi");
            AddAssert("the checksum lookup was field-restricted", () => mirror.LastSearch?.Option == "checksum");
            AddAssert("and searched on the replay's beatmap hash", () => mirror.LastSearch?.Query == mirror.RegisteredMd5);

            AddUntilStep("the resolved set is playing", () => playback.Current.Value?.SetId == 606060);

            AddAssert("the queued set carries the replay", () => queuedOrPlayingReplay()?.PlayerName == "Cookiezi");
            AddAssert("with real frames decoded", () => queuedOrPlayingReplay()?.Score?.Replay?.Frames.Count > 0);

            AddAssert("registered against the exact difficulty played", () =>
            {
                var attachment = queuedOrPlayingReplay();
                return attachment?.OsuFile != null && replayStore.ForOsuFile(attachment.OsuFile) == attachment;
            });

            AddUntilStep("playback selected that difficulty, not the set default", () =>
                playback.SelectedOsuFile.Value != null
                && playback.SelectedOsuFile.Value == queuedOrPlayingReplay()?.OsuFile);
        }

        [Test]
        public void AReplayWhoseBeatmapNoMirrorKnowsReportsAClearError()
        {
            string osr = null!;

            AddStep("build a replay for a beatmap no mirror serves", () =>
            {
                mirror.SetSearchResults(new List<BeatmapSetInfo>());
                osr = makeReplayFor("orphan", setId: 111222, player: "Ghost", publishToMirror: false);
            });

            AddStep("drop it", () => importer.Import(osr));

            AddUntilStep("failure names the player and the checksum", () => lastMessage.StartsWith("No beatmap found for Ghost's replay"));
            AddAssert("nothing queued", () => queue.Items.Count == 0);
        }

        [Test]
        public void DroppingSomethingThatIsNotAReplayReportsIt()
        {
            string osr = null!;

            AddStep("write a bogus .osr", () =>
            {
                osr = Path.Combine(tmp, "bogus.osr");
                File.WriteAllText(osr, "definitely not a replay");
            });

            AddStep("drop it", () => importer.Import(osr));
            AddUntilStep("reported as unreadable", () => lastMessage.StartsWith("Not a readable replay:"));
            AddAssert("nothing queued", () => queue.Items.Count == 0);
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

        /// <summary>The replay attachment for the dropped set, wherever it currently is — still
        /// queued, or already the now-playing set.</summary>
        private ReplayAttachment? queuedOrPlayingReplay()
            => jukebox.NowPlaying.Value?.Replay ?? queue.Items.Select(i => i.Replay).FirstOrDefault(r => r != null);

        /// <summary>
        /// Builds a two-difficulty set, a .osz for it, and a genuine .osr recorded on the set's
        /// SECOND difficulty — deliberately not the one <see cref="CachedBeatmapSet.PreferredOsuFile"/>
        /// would pick, so "playback selects the difficulty the replay was played on" is actually
        /// under test rather than trivially true.
        /// </summary>
        /// <param name="name">Base name for the fixture's build directory, .osz and .osr.</param>
        /// <param name="setId">Beatmapset id the mirror serves the archive under.</param>
        /// <param name="player">Player name recorded into the replay.</param>
        /// <param name="publishToMirror">False leaves the mirror with no result for the checksum,
        /// exercising the "no beatmap found" path.</param>
        private string makeReplayFor(string name, int setId, string player, bool publishToMirror = true)
        {
            string dir = Path.Combine(tmp, "build_" + name);
            Directory.CreateDirectory(dir);

            // Sorted first, so this is the set's default difficulty — and NOT the one played.
            File.WriteAllText(Path.Combine(dir, "map [Easy].osu"), osuWithObjects("Easy"));

            string played = Path.Combine(dir, "map [Hard].osu");
            File.WriteAllText(played, osuWithObjects("Hard"));

            writeSilentWav(Path.Combine(dir, "audio.wav"), 1);

            string osz = Path.Combine(tmp, name + ".osz");
            ZipFile.CreateFromDirectory(dir, osz);

            if (publishToMirror)
            {
                mirror.Publish(setId, osz,
                    new BeatmapSetInfo { Id = setId, Title = "Replayed Song", Artist = "Some Artist", Creator = "Some Mapper" },
                    ReplayFixture.Md5OfFile(played));
            }

            string osr = Path.Combine(tmp, name + ".osr");
            ReplayFixture.Write(osr, played, player);
            return osr;
        }

        private static string osuWithObjects(string version) =>
            "osu file format v14\n\n"
            + "[General]\nAudioFilename: audio.wav\nMode: 0\n\n"
            + $"[Metadata]\nTitle:Replayed Song\nArtist:Some Artist\nCreator:Some Mapper\nVersion:{version}\n\n"
            + "[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:8\nApproachRate:9\nSliderMultiplier:1.4\nSliderTickRate:1\n\n"
            + "[TimingPoints]\n0,500,4,2,0,60,1,0\n\n"
            + "[HitObjects]\n256,192,1000,1,0,0:0:0:0:\n128,96,1500,1,0,0:0:0:0:\n320,240,2000,1,0,0:0:0:0:\n";

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

        /// <summary>No search results, and any download attempt fails.</summary>
        private class EmptyMirror : IBeatmapMirror
        {
            public string Name => "empty";

            public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
                => Task.FromResult(new List<BeatmapSetInfo>());

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
                => throw new IOException($"no mirror download expected (set {setId})");
        }

        /// <summary>
        /// Serves a scripted search result and the .osz behind it, and records the last request so
        /// a test can assert HOW the lookup was issued (the checksum search is field-restricted —
        /// a mirror that answered a plain text query would look identical from the outside).
        /// </summary>
        private class FixtureMirror : IBeatmapMirror
        {
            private readonly Dictionary<int, string> archives = new();
            private List<BeatmapSetInfo> searchResults = new();

            public string Name => "fixture";

            /// <summary>The last request this mirror was asked to search for; null before any.</summary>
            public SearchRequest? LastSearch { get; private set; }

            /// <summary>The beatmap checksum the currently-registered set's replay was recorded on.</summary>
            public string? RegisteredMd5 { get; private set; }

            public void Reset()
            {
                archives.Clear();
                searchResults = new List<BeatmapSetInfo>();
                LastSearch = null;
                RegisteredMd5 = null;
            }

            public void Publish(int setId, string oszPath, BeatmapSetInfo set, string beatmapMd5)
            {
                archives[setId] = oszPath;
                searchResults = new List<BeatmapSetInfo> { set };
                RegisteredMd5 = beatmapMd5;
            }

            public void SetSearchResults(List<BeatmapSetInfo> sets) => searchResults = sets;

            public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
            {
                LastSearch = request;
                return Task.FromResult(searchResults);
            }

            public async Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
            {
                if (!archives.TryGetValue(setId, out string? path))
                    throw new IOException($"fixture mirror has no set {setId}");

                await using var fs = File.OpenRead(path);
                await fs.CopyToAsync(destination, ct);
            }
        }
    }
}
