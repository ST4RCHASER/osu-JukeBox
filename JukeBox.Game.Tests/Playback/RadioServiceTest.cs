using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using NUnit.Framework;
using osu.Framework.Bindables;

namespace JukeBox.Game.Tests.Playback
{
    public class ListMirror : IBeatmapMirror
    {
        private readonly List<BeatmapSetInfo> items;
        public string Name => "list";
        public bool Fail;
        public int SearchCalls;

        /// <summary>Every request this mirror was handed, so a test can assert what the radio
        /// actually ASKED rather than only what came back.</summary>
        public readonly List<SearchRequest> Requests = new List<SearchRequest>();

        public ListMirror(List<BeatmapSetInfo> items) => this.items = items;

        public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest r, CancellationToken ct = default)
        {
            SearchCalls++;
            Requests.Add(r);
            if (Fail) throw new IOException("down");
            return Task.FromResult(items);
        }

        public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback progress = null)
            => throw new System.NotSupportedException();
    }

    /// <summary>
    /// A mirror shaped like osu.direct: it takes a keyword and nothing else, and — the part that
    /// matters — returns NOTHING for an empty query, however many other filters the request carries.
    /// A random-pick strategy expressed only as page/status/sort finds nothing here.
    /// </summary>
    public class KeywordOnlyMirror : IBeatmapMirror
    {
        private readonly List<BeatmapSetInfo> items;
        public string Name => "keyword-only";
        public readonly List<string> Queries = new List<string>();

        /// <summary>The requests themselves, for asserting what was left OUT of them.</summary>
        public readonly List<SearchRequest> SeenRequests = new List<SearchRequest>();

        public KeywordOnlyMirror(List<BeatmapSetInfo> items) => this.items = items;

        public SearchFilters SupportedFilters => SearchFilters.Keyword;

        public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest r, CancellationToken ct = default)
        {
            Queries.Add(r.Query);
            SeenRequests.Add(r);
            return Task.FromResult(string.IsNullOrEmpty(r.Query) ? new List<BeatmapSetInfo>() : items);
        }

        public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback progress = null)
            => throw new System.NotSupportedException();
    }

    /// <summary>
    /// Stands in for osu!'s API: mints a token, then answers the search. Enough of the real schema
    /// for <see cref="BeatmapSetInfo.ParseList"/>, which is the same parser the mirrors' responses
    /// go through.
    /// </summary>
    public class FakeOfficialHandler : HttpMessageHandler
    {
        public int SearchCalls;
        public bool Fail;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.ToString();

            if (url.Contains("oauth/token"))
                return Task.FromResult(json("{\"access_token\":\"t\",\"expires_in\":86400}"));

            SearchCalls++;

            if (Fail)
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));

            return Task.FromResult(json("{\"beatmapsets\":[{\"id\":99,\"title\":\"From osu!\",\"artist\":\"A\"}],\"total\":1}"));
        }

        private static HttpResponseMessage json(string body) => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
    }

    [TestFixture]
    public class RadioServiceTest
    {
        private static OfficialBeatmapSearch officialSearch(FakeOfficialHandler handler)
            => new OfficialBeatmapSearch(new HttpClient(handler),
                new Bindable<string>("id"), new Bindable<string>("secret"));

        // The actual fix for the reported loop. Every mirror SEARCH was down (NeriNyan 530, catboy
        // TLS-blocked on macOS, osu.direct 502) while osu!'s own API answered fine — and the user
        // had already selected it for the listing. The radio was wired to the mirrors alone, so it
        // reported "nothing available" on a machine that could find music perfectly well.
        [Test]
        public async Task TheRadioUsesOsusOwnSearchWhenTheMirrorsAreDown()
        {
            var handler = new FakeOfficialHandler();
            var mirror = new ListMirror(new List<BeatmapSetInfo>()) { Fail = true };

            var radio = new RadioService(mirror, (min, max) => min, officialSearch(handler),
                new Bindable<SearchApi>(SearchApi.Official));

            var pick = await radio.PickRandomAsync();

            Assert.That(pick.Set?.Id, Is.EqualTo(99));
            Assert.That(mirror.SearchCalls, Is.Zero, "the mirror should not have been asked at all");
        }

        // ...and an official failure still falls through to the mirrors, same contract the listing
        // uses — a bad credential or a rate limit must never dead-end the radio.
        [Test]
        public async Task AnOfficialFailureFallsBackToTheMirror()
        {
            var handler = new FakeOfficialHandler { Fail = true };
            var mirror = new ListMirror(new List<BeatmapSetInfo> { new BeatmapSetInfo { Id = 5 } });

            var radio = new RadioService(mirror, (min, max) => min, officialSearch(handler),
                new Bindable<SearchApi>(SearchApi.Official));

            var pick = await radio.PickRandomAsync();

            Assert.That(pick.Set?.Id, Is.EqualTo(5));
            Assert.That(handler.SearchCalls, Is.GreaterThan(0), "the official API should have been tried first");
        }

        // With the mirror backend selected, osu!'s API must not be reached for at all — the setting
        // is the user's choice, and osu!'s terms ask for restraint on request volume.
        [Test]
        public async Task TheMirrorSettingIsRespected()
        {
            var handler = new FakeOfficialHandler();
            var mirror = new ListMirror(new List<BeatmapSetInfo> { new BeatmapSetInfo { Id = 5 } });

            var radio = new RadioService(mirror, (min, max) => min, officialSearch(handler),
                new Bindable<SearchApi>(SearchApi.Mirror));

            var pick = await radio.PickRandomAsync();

            Assert.That(pick.Set?.Id, Is.EqualTo(5));
            Assert.That(handler.SearchCalls, Is.Zero);
        }

        // The live failure this round came from: every mirror SEARCH was down and osu.direct — the
        // one the user had selected — answers only a keyword. The radio asked with an empty query
        // and got nothing back, three times, forever. Randomness now travels as a keyword plus a
        // sort, the two things every backend can express.
        [Test]
        public async Task RadioFindsSomethingThroughAKeywordOnlyMirror()
        {
            var mirror = new KeywordOnlyMirror(new List<BeatmapSetInfo> { new BeatmapSetInfo { Id = 7 } });
            var radio = new RadioService(mirror, (min, max) => min);

            var pick = await radio.PickRandomAsync();

            Assert.That(pick.Set?.Id, Is.EqualTo(7));
            Assert.That(mirror.Queries, Is.Not.Empty);
            Assert.That(mirror.Queries[0], Is.Not.Empty, "the radio must carry a keyword, or a keyword-only backend has nothing to match on");
        }

        // Consecutive picks must not all be the same set, or a "radio" plays one song forever. The
        // official API cannot take a page NUMBER (it pages by opaque cursor), so the variety has to
        // come from the request itself rather than from paging.
        [Test]
        public async Task ConsecutivePicksVaryTheirSearch()
        {
            var mirror = new KeywordOnlyMirror(new List<BeatmapSetInfo> { new BeatmapSetInfo { Id = 1 } });
            int n = 0;
            var radio = new RadioService(mirror, (min, max) => (n++) % max);

            for (int i = 0; i < 4; i++)
                await radio.PickRandomAsync();

            Assert.That(mirror.Queries.Distinct().Count(), Is.GreaterThan(1), "every pick asked the same question");
        }

        // When nothing is reachable, sets already on disk are still playable — and playing one is a
        // better answer than an error the user can do nothing about.
        [Test]
        public async Task FallsBackToACachedSetWhenNoSourceIsReachable()
        {
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(dir, "4242"));
            File.WriteAllText(Path.Combine(dir, "4242", "map.osu"), "osu file format v14");

            var mirror = new ListMirror(new List<BeatmapSetInfo>()) { Fail = true };
            var cache = new BeatmapCache(dir, mirror);
            var radio = new RadioService(mirror, (min, max) => min, cache: cache);

            var pick = await radio.PickRandomAsync();

            Assert.That(pick.Set?.Id, Is.EqualTo(4242));
            Assert.That(pick.FromCache, Is.True);
            Assert.That(pick.Failure, Is.Null, "a pick that succeeded from cache is not a failure");
        }

        // With no cache either, the reason has to reach the user — "no tracks available" said
        // nothing about a network that is simply unreachable.
        [Test]
        public async Task AnUnreachableNetworkIsReportedAsSuch()
        {
            var mirror = new ListMirror(new List<BeatmapSetInfo>()) { Fail = true };
            var radio = new RadioService(mirror, (min, max) => min);

            var pick = await radio.PickRandomAsync();

            Assert.That(pick.Set, Is.Null);
            Assert.That(pick.Failure, Does.Contain("reach"));
        }


        [Test]
        public async Task RadioSkipsDownloadDisabled()
        {
            var mirror = new ListMirror(new List<BeatmapSetInfo>
            {
                new BeatmapSetInfo { Id = 1, Availability = new AvailabilityInfo { DownloadDisabled = true } },
                new BeatmapSetInfo { Id = 2 },
            });
            var radio = new RadioService(mirror, (min, max) => min); // deterministic rng
            var pick = await radio.PickRandomAsync();
            Assert.That(pick.Set!.Id, Is.EqualTo(2));
        }

        [Test]
        public async Task ReturnsNullAfterThreeFailedAttempts()
        {
            var mirror = new ListMirror(new List<BeatmapSetInfo>()) { Fail = true };
            var radio = new RadioService(mirror, (min, max) => min);
            var pick = await radio.PickRandomAsync();
            Assert.That(pick.Set, Is.Null);
            Assert.That(pick.Failure, Is.Not.Null);
            Assert.That(mirror.SearchCalls, Is.EqualTo(3));
        }

        // ---- Station filters -------------------------------------------------------------------

        /// <summary>Writes a cached set on disk whose only difficulty declares
        /// <paramref name="mode"/> — enough for the fallback's ruleset check to read.</summary>
        private static void writeCachedSet(string root, int setId, int mode)
        {
            string dir = Path.Combine(root, setId.ToString());
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "map.osu"),
                $"osu file format v14\n\n[General]\nAudioFilename: audio.mp3\nMode: {mode}\n\n[Metadata]\nVersion: Test\n");
        }

        /// <summary>
        /// The whole point of the feature: a station's conditions have to reach the SEARCH, not
        /// just sit in config. Asserted on the request the mirror was handed, because a radio whose
        /// filters were merely stored would behave identically to one with no filters at all.
        /// </summary>
        [Test]
        public async Task TheStationsFiltersReachTheSearchRequest()
        {
            var mirror = new ListMirror(new List<BeatmapSetInfo> { new BeatmapSetInfo { Id = 5 } });
            var filters = new RadioFilters();

            filters.Mode.Value = RadioRuleset.Mania;
            filters.Category.Value = osu.Game.Overlays.BeatmapListing.SearchCategory.Loved;
            filters.MinStars.Value = 4;
            filters.MaxStars.Value = 6;

            var radio = new RadioService(mirror, (min, max) => min, filters: filters);

            await radio.PickRandomAsync();

            var request = mirror.Requests[0];

            Assert.That(request.Mode, Is.EqualTo("m"));
            Assert.That(request.Status, Is.EqualTo("loved"));
            Assert.That(request.MinStars, Is.EqualTo(4));
            Assert.That(request.MaxStars, Is.EqualTo(6));

            // The keyword is still what carries the randomness — filters narrow the pool, they
            // don't replace the mechanism that makes consecutive picks differ.
            Assert.That(request.Query, Is.Not.Empty);
        }

        /// <summary>
        /// A backend that can't express a filter must be sent a request WITHOUT it. Sending it
        /// anyway doesn't merely get it ignored — it makes the request unservable
        /// (<see cref="SearchRequest.RequiredFilters"/>), so the chain skips every mirror and the
        /// radio drops to its cache for a filter that was never going to work.
        /// </summary>
        [Test]
        public async Task FiltersAKeywordOnlyBackendCannotExpressAreNotSent()
        {
            var mirror = new KeywordOnlyMirror(new List<BeatmapSetInfo> { new BeatmapSetInfo { Id = 7 } });
            var filters = new RadioFilters();

            filters.Mode.Value = RadioRuleset.Mania;
            filters.Category.Value = osu.Game.Overlays.BeatmapListing.SearchCategory.Loved;
            filters.MinStars.Value = 4;

            var radio = new RadioService(mirror, (min, max) => min, filters: filters);

            var pick = await radio.PickRandomAsync();

            Assert.That(pick.Set?.Id, Is.EqualTo(7));

            var request = mirror.SeenRequests[0];

            Assert.That(request.Mode, Is.Null);
            Assert.That(request.MinStars, Is.Null);
            Assert.That(request.Status, Is.EqualTo(SearchRequest.ANY_STATUS));
            Assert.That(request.RequiredFilters, Is.EqualTo(SearchFilters.Keyword));
        }

        /// <summary>
        /// Nothing is reachable, so the pick comes off disk — and the mode filter is still
        /// honoured, because a set's ruleset is one of the few things a cached folder can actually
        /// answer (its .osu headers carry it).
        /// </summary>
        [Test]
        public async Task TheCachedFallbackHonoursTheModeFilterWhenItCan()
        {
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            writeCachedSet(dir, 100, mode: 0); // osu!
            writeCachedSet(dir, 200, mode: 3); // mania

            var mirror = new ListMirror(new List<BeatmapSetInfo>()) { Fail = true };
            var cache = new BeatmapCache(dir, mirror);
            var filters = new RadioFilters();
            filters.Mode.Value = RadioRuleset.Mania;

            // The candidate ORDER is the filesystem's, not ours, so the rng is pinned by looking the
            // indices up rather than by assuming them. Cache draws are told apart from the search's
            // own keyword/page/sort draws by their RANGE — only the cache asks for one of
            // ids.Count — because those run first and would otherwise eat the sequence.
            //
            // The first cache draw is the unfiltered fallback, aimed squarely at the osu! set: an
            // implementation that skipped the mode check therefore returns 100 every time, rather
            // than only when the directory happened to enumerate that way. Later draws are probes,
            // aimed at the mania set.
            var ids = cache.CachedSetIds().ToList();
            int osuIndex = ids.IndexOf(100);
            int maniaIndex = ids.IndexOf(200);

            int cacheDraws = 0;
            var radio = new RadioService(mirror,
                (min, max) => max != ids.Count ? min : (cacheDraws++ == 0 ? osuIndex : maniaIndex),
                cache: cache, filters: filters);

            var pick = await radio.PickRandomAsync();

            Assert.That(pick.FromCache, Is.True);
            Assert.That(pick.Set?.Id, Is.EqualTo(200), "the cached fallback ignored the mode filter");
            Assert.That(pick.CacheFilterRelaxed, Is.False);
        }

        /// <summary>
        /// ...but a filter that matches NOTHING on disk must not turn the fallback into silence.
        /// The fallback exists precisely for the case where the user can do nothing about the
        /// network; refusing to play the sets they already have because none is the right ruleset
        /// would trade its entire purpose for a filter they can loosen once they can hear anything.
        /// The relaxation is reported rather than hidden.
        /// </summary>
        [Test]
        public async Task TheCachedFallbackRelaxesTheModeFilterRatherThanPlayingNothing()
        {
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            writeCachedSet(dir, 100, mode: 0);
            writeCachedSet(dir, 101, mode: 0);

            var mirror = new ListMirror(new List<BeatmapSetInfo>()) { Fail = true };
            var filters = new RadioFilters();
            filters.Mode.Value = RadioRuleset.Mania;

            var radio = new RadioService(mirror, (min, max) => min,
                cache: new BeatmapCache(dir, mirror), filters: filters);

            var pick = await radio.PickRandomAsync();

            Assert.That(pick.Set, Is.Not.Null, "an unmatchable filter silenced the cache fallback");
            Assert.That(pick.FromCache, Is.True);
            Assert.That(pick.CacheFilterRelaxed, Is.True, "the relaxation has to be reportable, or the filter looks broken");
        }

        /// <summary>
        /// A neutral station takes whatever the cache offers and, crucially, does NOT report the
        /// relaxation — "we ignored your mode filter" would be a lie when there was no filter, and
        /// it reaches the user as a toast.
        /// </summary>
        [Test]
        public async Task ANeutralStationTakesTheCachedPickWithoutClaimingARelaxation()
        {
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            writeCachedSet(dir, 100, mode: 0);

            var mirror = new ListMirror(new List<BeatmapSetInfo>()) { Fail = true };
            var radio = new RadioService(mirror, (min, max) => min, cache: new BeatmapCache(dir, mirror));

            var pick = await radio.PickRandomAsync();

            Assert.That(pick.Set?.Id, Is.EqualTo(100));
            Assert.That(pick.CacheFilterRelaxed, Is.False);
        }
    }
}
