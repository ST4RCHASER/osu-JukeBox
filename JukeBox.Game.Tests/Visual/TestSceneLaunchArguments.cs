#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Import;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Command-line arguments end to end: each kind resolved, queued IN THE ORDER TYPED, and one
    /// bad argument never costing the user the rest of the batch.
    /// </summary>
    [TestFixture]
    public partial class TestSceneLaunchArguments : JukeBoxTestScene
    {
        private string tmp = null!;

        private MusicQueue queue = null!;
        private BeatmapCache cache = null!;
        private PlaybackController playback = null!;
        private Jukebox jukebox = null!;
        private GatedMirror mirror = null!;
        private DroppedFileImporter files = null!;
        private OfficialStub officialStub = null!;
        private LaunchArgumentImporter arguments = null!;

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            queue = new MusicQueue();
            mirror = new GatedMirror();
            cache = new BeatmapCache(Path.Combine(tmp, "cache"), mirror);
            playback = new PlaybackController();

            // The radio gets its own always-empty mirror so a background pick can never queue
            // something no test asked for — the queue order assertions below would not survive it.
            jukebox = new Jukebox(queue, new RadioService(new EmptyMirror()), cache, playback);

            // Cached, not merely added: LaunchArgumentImporter RESOLVES the file importer, and
            // without this it would find the test runner's own — which is wired to the runner's
            // cache and jukebox, so every import would land in a queue no assertion here can see.
            files = new DroppedFileImporter();

            // The runner caches an OfficialBeatmapSearch with no credentials, which is right for
            // most tests but leaves the credentialed path untestable. Override it with one whose
            // credentials and responses this fixture controls.
            officialStub = new OfficialStub();

            var dependencies = new DependencyContainer(base.CreateChildDependencies(parent));
            dependencies.CacheAs(officialStub.Search);
            dependencies.CacheAs(files);
            dependencies.CacheAs(queue);
            dependencies.CacheAs(cache);
            dependencies.CacheAs(playback);
            dependencies.CacheAs(jukebox);
            dependencies.CacheAs<IBeatmapMirror>(mirror);
            return dependencies;
        }

        private Container importerHost = null!;

        protected override void LoadComplete()
        {
            base.LoadComplete();
            Add(playback);
            Add(jukebox);
            Add(files);
            Add(importerHost = new Container { RelativeSizeAxes = Axes.Both });
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("reset playback, queue and mirror", () =>
            {
                playback.Stop();
                playback.Current.Value = null;
                playback.SelectedOsuFile.Value = null;
                jukebox.NowPlaying.Value = null;
                queue.Items.Clear();
                mirror.Reset();
                officialStub.Reset();

                // Occupy the playback slot with a stand-in rather than actually playing something.
                // The jukebox plays the first thing handed to an IDLE queue (EnqueueAndMaybePlayAsync
                // pops it straight back off), so without this the first argument of every batch would
                // be missing from the queue it is really being played from. A dummy is enough — the
                // check is only Current != null — and it keeps this fixture off real audio and the
                // lazer resource/realm machinery entirely, which is not what these tests are about.
                playback.Current.Value = new CachedBeatmapSet { SetId = -999, Directory = tmp };
            });

            AddStep("create the argument importer", () =>
            {
                messages.Clear();
                importerHost.Child = arguments = new LaunchArgumentImporter();

                // Every notification is recorded, not just the latest: a batch reports one per
                // argument, so the toast naming a FAILED argument is immediately overwritten by
                // the next argument's success.
                arguments.Notification.BindValueChanged(e =>
                {
                    if (e.NewValue is { } outcome)
                        messages.Add(outcome.Message);
                });
            });
        }

        private readonly List<string> messages = new List<string>();

        private string lastMessage => arguments.Notification.Value?.Message ?? string.Empty;

        private List<int> queuedIds => queue.Items.Select(i => i.Id).ToList();

        /// <summary>
        /// The heart of the feature: queue order follows ARGUMENT order, not completion order.
        ///
        /// <para>
        /// Rigged so parallel handling would demonstrably get it wrong — the first argument is a
        /// set the mirror will not answer until released, while the second is a local file that
        /// would import immediately. If the two were processed concurrently the fast one would
        /// reach the queue first.
        /// </para>
        /// </summary>
        [Test]
        public void ArgumentsQueueInTheOrderTypedEvenWhenTheyFinishOutOfOrder()
        {
            string osz = null!;
            Task batch = null!;

            AddStep("publish a slow set and build a fast local file", () =>
            {
                mirror.Publish(new BeatmapSetInfo { Id = 100, Title = "Slow" }, makeOsz(100, "Slow"));
                mirror.HoldSearches();
                osz = makeOsz(200, "Fast");
            });

            AddStep("hand over [slow set id, fast local file]", () => batch = arguments.HandleAsync(new[] { "100", osz }));

            AddUntilStep("the slow lookup is in flight", () => mirror.Waiting);
            AddAssert("nothing is queued yet", () => queuedIds.Count == 0);
            AddAssert("and the fast file has NOT jumped ahead", () => !queuedIds.Contains(200));

            AddStep("release the mirror", () => mirror.ReleaseSearches());
            AddUntilStep("both are queued", () => batch.IsCompleted && queuedIds.Count == 2);

            AddAssert("in the order they were typed", () => queuedIds.SequenceEqual(new[] { 100, 200 }));
        }

        [Test]
        public void OneBadArgumentIsReportedAndTheRestStillRun()
        {
            Task batch = null!;

            AddStep("publish a set", () => mirror.Publish(new BeatmapSetInfo { Id = 100, Title = "Good" }, makeOsz(100, "Good")));

            AddStep("hand over [nonsense, good set]", () => batch = arguments.HandleAsync(new[] { "not-an-argument", "100" }));

            AddUntilStep("batch finished", () => batch.IsCompleted);
            AddAssert("the good one still got queued", () => queuedIds.SequenceEqual(new[] { 100 }));
            AddUntilStep("and the bad one was named", () => messages.Any(m => m.Contains("not-an-argument")));
        }

        [Test]
        public void AMissingFileIsReportedByName()
        {
            Task batch = null!;

            AddStep("hand over a path that does not exist",
                () => batch = arguments.HandleAsync(new[] { Path.Combine(tmp, "nope.osz") }));

            AddUntilStep("batch finished", () => batch.IsCompleted);
            AddUntilStep("reported as missing", () => lastMessage.Contains("No such file") && lastMessage.Contains("nope.osz"));
            AddAssert("nothing queued", () => queuedIds.Count == 0);
        }

        // A local .osz goes through the SAME importer a dropped one does, so it lands in the queue
        // with the metadata read out of the archive rather than by some parallel path.
        [Test]
        public void ALocalBeatmapArchiveIsImportedThroughTheDropImporter()
        {
            string osz = null!;
            Task batch = null!;

            AddStep("build an archive", () => osz = makeOsz(4242, "Local Song"));
            AddStep("hand it over", () => batch = arguments.HandleAsync(new[] { osz }));

            AddUntilStep("queued", () => batch.IsCompleted && queuedIds.SequenceEqual(new[] { 4242 }));
            AddAssert("and it is cached on disk", () => cache.IsCached(4242));
        }

        [Test]
        public void AnUnknownSetIsReportedByItsId()
        {
            Task batch = null!;

            AddStep("mirror knows nothing", () => mirror.SetSearchResults(new List<BeatmapSetInfo>()));
            AddStep("hand over a set id", () => batch = arguments.HandleAsync(new[] { "999" }));

            AddUntilStep("batch finished", () => batch.IsCompleted);
            AddUntilStep("reported by id", () => lastMessage.Contains("999"));
            AddAssert("nothing queued", () => queuedIds.Count == 0);
        }

        // Credentialed, but osu! has no such beatmap. Distinct from the no-credentials case: the
        // lookup really ran and really came back empty, so the answer is about the beatmap, not
        // about configuration.
        [Test]
        public void ADifficultyLinkOsuDoesNotKnowIsReportedAsSuch()
        {
            Task batch = null!;

            AddStep("credentials, but osu! knows nothing", () => officialStub.SetCredentials(true));
            AddStep("hand over a /b/ link", () => batch = arguments.HandleAsync(new[] { "https://osu.ppy.sh/b/67890" }));

            AddUntilStep("batch finished", () => batch.IsCompleted);
            AddUntilStep("reported as an unknown beatmap", () => messages.Any(m => m.Contains("osu! has no beatmap 67890")));
            AddAssert("it did ask osu!", () => officialStub.Requested.SequenceEqual(new[] { 67890 }));
            AddAssert("and never asked the mirror for a set", () => mirror.Searches == 0);
        }

        // A set link and a bare id are the same instruction written two ways.
        [Test]
        public void ASetLinkQueuesTheSameThingABareIdDoes()
        {
            Task batch = null!;

            AddStep("publish a set", () => mirror.Publish(new BeatmapSetInfo { Id = 100, Title = "Linked" }, makeOsz(100, "Linked")));
            AddStep("hand over the set link", () => batch = arguments.HandleAsync(new[] { "https://osu.ppy.sh/beatmapsets/100" }));

            AddUntilStep("queued", () => batch.IsCompleted && queuedIds.SequenceEqual(new[] { 100 }));
        }

        // The app's own switches are not content and must never reach the queue or the toasts.
        [Test]
        public void SwitchesArePassedOverEntirely()
        {
            Task batch = null!;

            AddStep("publish a set", () => mirror.Publish(new BeatmapSetInfo { Id = 100, Title = "Only" }, makeOsz(100, "Only")));
            AddStep("hand over [--viewer, set id]", () => batch = arguments.HandleAsync(new[] { "--viewer", "100" }));

            AddUntilStep("batch finished", () => batch.IsCompleted);
            AddAssert("only the real argument queued", () => queuedIds.SequenceEqual(new[] { 100 }));
            AddAssert("and no switch was reported as a failure", () => messages.All(m => !m.Contains("--viewer")));
        }

        [Test]
        public void AnEmptyBatchDoesNothing()
        {
            Task batch = null!;

            AddStep("hand over nothing", () => batch = arguments.HandleAsync(Array.Empty<string>()));

            AddUntilStep("batch finished", () => batch.IsCompleted);
            AddAssert("nothing queued", () => queuedIds.Count == 0);
            AddAssert("nothing reported", () => messages.Count == 0);
        }

        // ---- beatmap id -> set (osu! API) -----------------------------------------------------

        // Without credentials the app CANNOT resolve a difficulty link — no mirror offers a
        // beatmap-id endpoint. The message must say that, not imply the map does not exist.
        [Test]
        public void ADifficultyLinkWithoutCredentialsSaysCredentialsAreWhatIsMissing()
        {
            Task batch = null!;

            AddStep("no credentials", () => officialStub.SetCredentials(false));
            AddStep("hand over a /b/ link", () => batch = arguments.HandleAsync(new[] { "https://osu.ppy.sh/b/67890" }));

            AddUntilStep("batch finished", () => batch.IsCompleted);
            AddUntilStep("credentials named as the gap", () => messages.Any(m => m.Contains("osu! API credentials")));
            AddAssert("the mirror was never asked", () => mirror.Searches == 0);
            AddAssert("nothing queued", () => queuedIds.Count == 0);
        }

        // With credentials it resolves the difficulty to its set and queues that.
        [Test]
        public void ADifficultyLinkResolvesThroughTheOfficialApiAndQueuesItsSet()
        {
            Task batch = null!;

            AddStep("credentials, and osu! knows the beatmap", () =>
            {
                officialStub.SetCredentials(true);
                officialStub.Resolve(67890, 100);
                mirror.Publish(new BeatmapSetInfo { Id = 100, Title = "From A Difficulty Link" }, makeOsz(100, "FromDiff"));
            });

            AddStep("hand over a /b/ link", () => batch = arguments.HandleAsync(new[] { "https://osu.ppy.sh/b/67890" }));

            AddUntilStep("the set it belongs to is queued", () => batch.IsCompleted && queuedIds.SequenceEqual(new[] { 100 }));
            AddAssert("looked up the beatmap that was named", () => officialStub.Requested.SequenceEqual(new[] { 67890 }));
        }

        // A bare number is read as a SET id first, because that is what it usually is. Only when
        // that misses is it retried as a beatmap id — the two are indistinguishable as text.
        [Test]
        public void ABareIdThatIsNotASetIsRetriedAsABeatmapId()
        {
            Task batch = null!;

            AddStep("mirror has no such set, but osu! knows the beatmap", () =>
            {
                officialStub.SetCredentials(true);
                officialStub.Resolve(67890, 100);
                mirror.SetSearchResults(new List<BeatmapSetInfo>());
            });

            AddStep("hand over the bare id", () => batch = arguments.HandleAsync(new[] { "67890" }));

            AddUntilStep("batch finished", () => batch.IsCompleted);
            AddAssert("it was tried as a set first", () => mirror.Searches > 0);
            AddAssert("then as a beatmap", () => officialStub.Requested.SequenceEqual(new[] { 67890 }));
        }

        // ...and a bare id that is neither still reports the SET miss, which is the likelier
        // explanation, rather than a confusing note about credentials.
        [Test]
        public void ABareIdThatIsNeitherReportsTheSetMiss()
        {
            Task batch = null!;

            AddStep("nothing knows it", () =>
            {
                officialStub.SetCredentials(false);
                mirror.SetSearchResults(new List<BeatmapSetInfo>());
            });

            AddStep("hand over the bare id", () => batch = arguments.HandleAsync(new[] { "424242" }));

            AddUntilStep("batch finished", () => batch.IsCompleted);
            AddUntilStep("reported as a missing set", () => messages.Any(m => m.Contains("No beatmapset 424242")));
            AddAssert("and not as a credentials problem", () => messages.All(m => !m.Contains("credentials")));
        }

        /// <summary>An .osz carrying one difficulty, declaring <paramref name="setId"/>.</summary>
        private string makeOsz(int setId, string title)
        {
            string build = Path.Combine(tmp, "build-" + Path.GetRandomFileName());
            Directory.CreateDirectory(build);

            File.WriteAllText(Path.Combine(build, "map.osu"),
                "osu file format v14\n\n[General]\nAudioFilename: audio.mp3\nMode: 0\n\n"
                + $"[Metadata]\nTitle:{title}\nArtist:Someone\nCreator:Mapper\nVersion:Normal\nBeatmapSetID:{setId}\n");
            File.WriteAllBytes(Path.Combine(build, "audio.mp3"), new byte[] { 0xFF });

            // Unique per call: tmp is fixture-scoped, so a name derived from the title alone
            // collides the second time a test builds the same fixture.
            string osz = Path.Combine(tmp, $"{title}-{setId}-{Path.GetRandomFileName()}.osz");
            ZipFile.CreateFromDirectory(build, osz);
            return osz;
        }

        /// <summary>
        /// A real <see cref="OfficialBeatmapSearch"/> over a canned HTTP handler, so the fixture
        /// controls both whether credentials exist and what osu! answers — without ever reaching
        /// the network or needing anyone's actual OAuth application.
        /// </summary>
        private class OfficialStub
        {
            private readonly Bindable<string> id = new Bindable<string>(string.Empty);
            private readonly Bindable<string> secret = new Bindable<string>(string.Empty);
            private readonly Handler handler = new Handler();

            public OfficialStub()
            {
                Search = new OfficialBeatmapSearch(new HttpClient(handler), id, secret,
                    "https://stub.invalid/oauth/token", "https://stub.invalid/search", "https://stub.invalid/beatmaps/");
            }

            public OfficialBeatmapSearch Search { get; }

            /// <summary>Beatmap ids this stub was actually asked about.</summary>
            public List<int> Requested => handler.Requested;

            public void Reset()
            {
                SetCredentials(false);
                handler.Reset();
            }

            public void SetCredentials(bool present)
            {
                id.Value = present ? "client" : string.Empty;
                secret.Value = present ? "secret" : string.Empty;
            }

            public void Resolve(int beatmapId, int setId) => handler.Sets[beatmapId] = setId;

            private class Handler : HttpMessageHandler
            {
                public readonly Dictionary<int, int> Sets = new Dictionary<int, int>();
                public readonly List<int> Requested = new List<int>();

                public void Reset()
                {
                    Sets.Clear();
                    Requested.Clear();
                }

                protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                {
                    string url = request.RequestUri!.ToString();

                    if (url.Contains("/oauth/token"))
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{\"access_token\":\"t\",\"expires_in\":86400,\"token_type\":\"Bearer\"}"),
                        });
                    }

                    int beatmapId = int.Parse(url[(url.LastIndexOf('/') + 1)..], CultureInfo.InvariantCulture);
                    Requested.Add(beatmapId);

                    if (!Sets.TryGetValue(beatmapId, out int setId))
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"{{\"id\":{beatmapId},\"beatmapset_id\":{setId}}}"),
                    });
                }
            }
        }

        private class EmptyMirror : IBeatmapMirror
        {
            public string Name => "empty";

            public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
                => Task.FromResult(new List<BeatmapSetInfo>());

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
                => throw new IOException($"no mirror download expected (set {setId})");
        }

        /// <summary>
        /// Answers set lookups from a script, and can be made to HOLD an answer so a test can
        /// observe what the importer does while one argument is still in flight — which is the only
        /// way to tell sequential processing apart from parallel processing that happens to finish
        /// in a convenient order.
        /// </summary>
        private class GatedMirror : IBeatmapMirror
        {
            private readonly Dictionary<int, string> archives = new Dictionary<int, string>();
            private List<BeatmapSetInfo> searchResults = new List<BeatmapSetInfo>();
            private TaskCompletionSource? gate;

            public string Name => "gated";

            /// <summary>How many searches have been issued — 0 proves a path never queried at all.</summary>
            public int Searches { get; private set; }

            /// <summary>True once a search is parked on the gate.</summary>
            public bool Waiting { get; private set; }

            public void Reset()
            {
                archives.Clear();
                searchResults = new List<BeatmapSetInfo>();
                gate = null;
                Waiting = false;
                Searches = 0;
            }

            /// <summary>
            /// A set the mirror both FINDS and can serve. The archive matters: queueing a set
            /// prefetches it, and a mirror that answers the search but not the download turns a
            /// passing test into a logged download error.
            /// </summary>
            public void Publish(BeatmapSetInfo set, string oszPath)
            {
                archives[set.Id] = oszPath;
                searchResults = new List<BeatmapSetInfo> { set };
            }

            public void SetSearchResults(List<BeatmapSetInfo> sets) => searchResults = sets;

            public void HoldSearches() => gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            public void ReleaseSearches() => gate?.TrySetResult();

            public async Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
            {
                Searches++;

                if (gate != null)
                {
                    Waiting = true;
                    await gate.Task.ConfigureAwait(false);
                    Waiting = false;
                }

                return searchResults;
            }

            public async Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
            {
                if (!archives.TryGetValue(setId, out string? archive))
                    throw new IOException($"no archive published for set {setId}");

                await using var source = File.OpenRead(archive);
                await source.CopyToAsync(destination, ct).ConfigureAwait(false);
            }
        }
    }
}
