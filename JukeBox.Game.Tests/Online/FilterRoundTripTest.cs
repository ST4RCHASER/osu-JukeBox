#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Online
{
    /// <summary>
    /// Asserts filters at the URL, not at the bindable. Every filter row was correctly wired to its
    /// engine bindable while the app was nonetheless returning identical results for every filter
    /// change — because the mirror that answered took a keyword and nothing else. A bindable-level
    /// test passes happily through that, so these follow the value all the way to the request each
    /// mirror actually sends.
    /// </summary>
    [TestFixture]
    public class FilterRoundTripTest
    {
        /// <summary>Builds the request the engine would send with the given filters applied — the
        /// same path the listing's rows drive.</summary>
        private static SearchRequest requestFrom(string? mode = null, string category = "ranked",
                                                 bool video = false, bool storyboard = false,
                                                 string sortKey = "ranked", bool descending = true,
                                                 double minStars = 0, double maxStars = 10, int page = 0,
                                                 bool featuredArtists = false)
        {
            var engine = new BeatmapSearchEngine();

            engine.Query.Value = "camellia";
            engine.Mode.Value = mode;
            engine.Category.Value = category;
            engine.HasVideo.Value = video;
            engine.HasStoryboard.Value = storyboard;
            engine.SortKey.Value = sortKey;
            engine.SortDescending.Value = descending;
            engine.MinStars.Value = minStars;
            engine.MaxStars.Value = maxStars;
            engine.FeaturedArtists.Value = featuredArtists;

            return engine.BuildRequest(page);
        }

        // ---- NeriNyan: the one mirror that takes the whole filter vocabulary ------------------

        [Test]
        public void EveryFilterReachesTheNerinyanUrl()
        {
            Assert.That(NerinyanMirror.BuildSearchUrl(requestFrom(mode: "m")), Does.Contain("m=m"));
            Assert.That(NerinyanMirror.BuildSearchUrl(requestFrom(category: "loved")), Does.Contain("s=loved"));
            Assert.That(NerinyanMirror.BuildSearchUrl(requestFrom(video: true)), Does.Contain("e=video"));
            Assert.That(NerinyanMirror.BuildSearchUrl(requestFrom(storyboard: true)), Does.Contain("e=storyboard"));
            Assert.That(NerinyanMirror.BuildSearchUrl(requestFrom(video: true, storyboard: true)), Does.Contain("e=video.storyboard"));
            Assert.That(NerinyanMirror.BuildSearchUrl(requestFrom(sortKey: "plays")), Does.Contain("sort=plays_desc"));
            Assert.That(NerinyanMirror.BuildSearchUrl(requestFrom(sortKey: "title", descending: false)), Does.Contain("sort=title_asc"));
            Assert.That(NerinyanMirror.BuildSearchUrl(requestFrom(page: 2)), Does.Contain("p=2"));
        }

        [Test]
        public void StarRangeReachesTheNerinyanB64Body()
        {
            string url = NerinyanMirror.BuildSearchUrl(requestFrom(minStars: 4.5, maxStars: 6));

            // The legacy query string can't express a range at all, so a star filter has to switch
            // transports entirely — the one case where "the parameter is present" isn't the check.
            Assert.That(url, Does.Contain("b64="));

            string b64 = System.Uri.UnescapeDataString(url.Split("b64=")[1]);
            string json = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(b64));

            Assert.That(json, Does.Contain("\"min\":4.5"));
            Assert.That(json, Does.Contain("\"max\":6"));
        }

        // ---- catboy.best: ruleset, status and paging travel; nothing else does ----------------

        [Test]
        public void SupportedFiltersReachTheCatboyUrl()
        {
            Assert.That(CatboyMirror.BuildSearchUrl(requestFrom(mode: "m")), Does.Contain("mode=3"));
            Assert.That(CatboyMirror.BuildSearchUrl(requestFrom(mode: "t")), Does.Contain("mode=1"));
            Assert.That(CatboyMirror.BuildSearchUrl(requestFrom(category: "loved")), Does.Contain("status=4"));
            Assert.That(CatboyMirror.BuildSearchUrl(requestFrom(category: "graveyard")), Does.Contain("status=-2"));
            Assert.That(CatboyMirror.BuildSearchUrl(requestFrom(page: 2)), Does.Contain("offset=60"));

            // "Any" is the absence of a status filter, not a value.
            Assert.That(CatboyMirror.BuildSearchUrl(requestFrom(category: "all")), Does.Not.Contain("status="));
            Assert.That(CatboyMirror.BuildSearchUrl(requestFrom()), Does.Not.Contain("mode="));
            Assert.That(CatboyMirror.BuildSearchUrl(requestFrom(page: 0)), Does.Not.Contain("offset="));
        }

        [Test]
        public void CatboyAdmitsWhatItCannotFilter()
        {
            IBeatmapMirror catboy = new CatboyMirror(new System.Net.Http.HttpClient());

            Assert.That(catboy.SupportedFilters, Is.EqualTo(
                SearchFilters.Keyword | SearchFilters.Mode | SearchFilters.Status | SearchFilters.Paging));

            Assert.That(catboy.CanApplyFilters(requestFrom(mode: "m", category: "loved", page: 3)), Is.True);

            Assert.That(catboy.CanApplyFilters(requestFrom(video: true)), Is.False);
            Assert.That(catboy.CanApplyFilters(requestFrom(storyboard: true)), Is.False);
            Assert.That(catboy.CanApplyFilters(requestFrom(minStars: 5)), Is.False);
            Assert.That(catboy.CanApplyFilters(requestFrom(sortKey: "plays")), Is.False);

            // "Has Leaderboard" is four statuses at once, which this API has no single int for —
            // a value-level gap the per-filter flags deliberately cannot express.
            Assert.That(catboy.CanApplyFilters(requestFrom(category: "leaderboard")), Is.False);
        }

        // ---- osu.direct: a keyword and nothing else --------------------------------------------

        [Test]
        public void OsuDirectAdmitsItCannotFilterAtAll()
        {
            IBeatmapMirror osuDirect = new OsuDirectMirror(new System.Net.Http.HttpClient());

            Assert.That(osuDirect.SupportedFilters, Is.EqualTo(SearchFilters.Keyword));

            Assert.That(osuDirect.CanApplyFilters(requestFrom(category: "all")), Is.True);

            Assert.That(osuDirect.CanApplyFilters(requestFrom(category: "ranked")), Is.False);
            Assert.That(osuDirect.CanApplyFilters(requestFrom(category: "all", mode: "m")), Is.False);
            Assert.That(osuDirect.CanApplyFilters(requestFrom(category: "all", video: true)), Is.False);
            Assert.That(osuDirect.CanApplyFilters(requestFrom(category: "all", minStars: 5)), Is.False);
            Assert.That(osuDirect.CanApplyFilters(requestFrom(category: "all", sortKey: "plays")), Is.False);
            Assert.That(osuDirect.CanApplyFilters(requestFrom(category: "all", page: 1)), Is.False);
        }

        [Test]
        public void NerinyanTakesTheWholeMirrorVocabulary()
        {
            IBeatmapMirror nerinyan = new NerinyanMirror(new System.Net.Http.HttpClient());

            Assert.That(nerinyan.SupportedFilters, Is.EqualTo(SearchFilters.AllMirror));

            // Genre and language are osu-web concepts no mirror search exposes.
            Assert.That(nerinyan.SupportedFilters.HasFlag(SearchFilters.Genre), Is.False);
            Assert.That(nerinyan.SupportedFilters.HasFlag(SearchFilters.Language), Is.False);

            Assert.That(nerinyan.CanApplyFilters(requestFrom(
                mode: "m", category: "loved", video: true, storyboard: true, sortKey: "plays", minStars: 4, page: 2)), Is.True);
        }

        [Test]
        public void ARequestOnlyRequiresTheFiltersItActuallyExercises()
        {
            // A filter left at its neutral value asks nothing of the backend — which is what lets
            // osu.direct answer a plain keyword search at all.
            Assert.That(requestFrom(category: "all").RequiredFilters, Is.EqualTo(SearchFilters.Keyword));

            Assert.That(requestFrom(category: "all", mode: "m").RequiredFilters,
                Is.EqualTo(SearchFilters.Keyword | SearchFilters.Mode));

            Assert.That(requestFrom(category: "ranked").RequiredFilters,
                Is.EqualTo(SearchFilters.Keyword | SearchFilters.Status));

            Assert.That(requestFrom(category: "all", minStars: 5).RequiredFilters,
                Is.EqualTo(SearchFilters.Keyword | SearchFilters.Stars));

            Assert.That(requestFrom(category: "all", sortKey: "plays").RequiredFilters,
                Is.EqualTo(SearchFilters.Keyword | SearchFilters.Sort));

            Assert.That(requestFrom(category: "all", page: 2).RequiredFilters,
                Is.EqualTo(SearchFilters.Keyword | SearchFilters.Paging));
        }

        // ---- Official: the same values, in that endpoint's spelling -----------------------------

        [Test]
        public void EveryFilterReachesTheOfficialUrl()
        {
            string url = OfficialBeatmapSearch.BuildSearchUrl(requestFrom(
                mode: "m", category: "loved", video: true, storyboard: true,
                sortKey: "plays", minStars: 4.5, maxStars: 6));

            Assert.That(url, Does.Contain("m=3"));
            Assert.That(url, Does.Contain("s=loved"));
            Assert.That(url, Does.Contain("e=video.storyboard"));
            Assert.That(url, Does.Contain("sort=plays_desc"));
            Assert.That(url, Does.Contain("nsfw="));
            Assert.That(System.Uri.UnescapeDataString(url), Does.Contain("stars>=4.5 stars<=6"));
        }

        /// <summary>
        /// The Featured Artists filter rides osu-web's <c>c</c> parameter. Pinned at the URL
        /// because the obvious spelling is WRONG in a way nothing reports: verified live against
        /// osu.ppy.sh, <c>general=featured_artists</c> (and <c>general[]=</c>, and a dot-joined list
        /// under that name) returns byte-for-byte the same unfiltered page an unrecognised value
        /// returns — 1,247,030 results with 11 of the first 50 carrying a track_id — while
        /// <c>c=featured_artists</c> gives 99,171 with all 50 carrying one. A test asserting only
        /// that the bindable reached the request would have passed against the broken spelling.
        /// </summary>
        [Test]
        public void FeaturedArtistsReachesTheOfficialUrlAsTheCParameter()
        {
            string on = OfficialBeatmapSearch.BuildSearchUrl(requestFrom(featuredArtists: true));

            Assert.That(on, Does.Contain("c=featured_artists"));
            Assert.That(on, Does.Not.Contain("general="));

            // Off is the ABSENCE of the parameter, not `c=` with an empty value: osu-web splits
            // whatever it finds on dots, so an empty one would still be a (meaningless) list.
            Assert.That(OfficialBeatmapSearch.BuildSearchUrl(requestFrom()), Does.Not.Contain("c="));
        }

        [Test]
        public void FeaturedArtistsIsAnOfficialOnlyFilter()
        {
            Assert.That(SearchFilters.All.HasFlag(SearchFilters.FeaturedArtists), Is.True);
            Assert.That(SearchFilters.AllMirror.HasFlag(SearchFilters.FeaturedArtists), Is.False);

            // A request that asks for it is unservable by even the most capable mirror, rather than
            // being quietly answered unfiltered — no mirror serves the field it filters on, so
            // there is no client-side stand-in the way there is for genre and language.
            IBeatmapMirror nerinyan = new NerinyanMirror(new System.Net.Http.HttpClient());

            Assert.That(requestFrom(featuredArtists: true).RequiredFilters.HasFlag(SearchFilters.FeaturedArtists), Is.True);
            Assert.That(nerinyan.CanApplyFilters(requestFrom(featuredArtists: true)), Is.False);
        }

        /// <summary>
        /// The engine must not SEND a filter the active backend can't express — the row being
        /// hidden is only half of it. Without this the mirror path would build a request no mirror
        /// could serve out of a value the user can no longer even see.
        /// </summary>
        [Test]
        public void FeaturedArtistsIsLeftOutOfTheRequestOnTheMirrorBackend()
        {
            var engine = new BeatmapSearchEngine();

            engine.FeaturedArtists.Value = true;
            engine.AvailableFilters.Value = SearchFilters.AllMirror;

            Assert.That(engine.BuildRequest(0).FeaturedArtistsOnly, Is.False);

            engine.AvailableFilters.Value = SearchFilters.All;

            Assert.That(engine.BuildRequest(0).FeaturedArtistsOnly, Is.True);
        }

        // ---- The chain: a limited mirror must never quietly answer a filtered search ------------

        [Test]
        public async Task ChainPrefersAMirrorThatCanApplyTheFilters()
        {
            // Ordered as SwitchableMirror would with osu.direct preferred — the exact configuration
            // in which every filter silently stopped working.
            var limited = new FakeMirror("osu.direct", capable: false);
            var capable = new FakeMirror("NeriNyan", capable: true);
            var chain = new MirrorChain(limited, capable);

            var request = requestFrom(mode: "m");
            string? droppedBy = null;
            request.OnFiltersDropped = name => droppedBy = name;

            await chain.SearchAsync(request);

            Assert.That(capable.Searches, Is.EqualTo(1));
            Assert.That(limited.Searches, Is.Zero);
            Assert.That(droppedBy, Is.Null);
        }

        [Test]
        public async Task ChainStillAnswersWhenNoMirrorCanFilterButSaysSo()
        {
            var limited = new FakeMirror("osu.direct", capable: false);
            var brokenButCapable = new FakeMirror("NeriNyan", capable: true) { Broken = true };
            var chain = new MirrorChain(brokenButCapable, limited);

            var request = requestFrom(mode: "m");
            string? droppedBy = null;
            request.OnFiltersDropped = name => droppedBy = name;

            var results = await chain.SearchAsync(request);

            // Resilience is preserved — the user still gets results rather than an empty listing —
            // but they are told the results are broader than the filters they set.
            Assert.That(results, Is.Not.Empty);
            Assert.That(limited.Searches, Is.EqualTo(1));
            Assert.That(droppedBy, Is.EqualTo("osu.direct"));
        }

        private class FakeMirror : IBeatmapMirror
        {
            private readonly bool capable;

            public FakeMirror(string name, bool capable)
            {
                Name = name;
                this.capable = capable;
            }

            public string Name { get; }
            public int Searches;
            public bool Broken;

            public bool CanApplyFilters(SearchRequest request) => capable;

            public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
            {
                Searches++;

                if (Broken)
                    throw new System.Exception("mirror down");

                return Task.FromResult(new List<BeatmapSetInfo> { new BeatmapSetInfo { Id = 1 } });
            }

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
                => throw new System.NotSupportedException();
        }
    }
}
