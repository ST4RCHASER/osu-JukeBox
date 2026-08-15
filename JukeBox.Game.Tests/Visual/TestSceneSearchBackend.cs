#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Configuration;
using JukeBox.Game.Online;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Covers the "Search API" setting end-to-end below the settings panel: which backend
    /// <see cref="BeatmapSearchEngine"/> actually asks, how each one pages, that an official failure
    /// falls back to the mirror instead of dead-ending the listing, and that
    /// <see cref="FullscreenListingOverlay"/> only offers the filter rows the ACTIVE backend can
    /// answer. The official endpoint is a stubbed <see cref="HttpMessageHandler"/> — the real one
    /// needs credentials this suite doesn't have, and pinning the wire format is
    /// <see cref="Online.OfficialBeatmapSearchTest"/>'s job.
    /// </summary>
    [TestFixture]
    public partial class TestSceneSearchBackend : JukeBoxTestScene
    {
        private StubMirror mirror = null!;
        private StubOfficialHandler officialHandler = null!;
        private Bindable<string> clientId = null!;
        private Bindable<string> clientSecret = null!;

        private BeatmapSearchEngine engine = null!;
        private FullscreenListingOverlay listing = null!;

        private JukeBoxConfigManager config = null!;

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            var deps = new DependencyContainer(parent);

            // Isolated config (same approach as TestSceneSettingsOverlay): the engine binds its
            // backend to the persisted setting, so without this the tests would rewrite the real
            // user config.
            config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-search-backend-test", Path.GetRandomFileName())));
            deps.Cache(config);

            deps.CacheAs<IBeatmapMirror>(mirror = new StubMirror());

            clientId = new Bindable<string>("1234");
            clientSecret = new Bindable<string>("s3cret");
            officialHandler = new StubOfficialHandler();

            deps.Cache(new OfficialBeatmapSearch(new HttpClient(officialHandler), clientId, clientSecret,
                "https://stub.invalid/oauth/token", "https://stub.invalid/api/v2/beatmapsets/search"));

            return deps;
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("reset backends", () =>
            {
                mirror.Sets.Clear();
                mirror.Sets.Add(new BeatmapSetInfo { Id = 100, Title = "From Mirror", Genre = new NamedIdInfo { Id = 3 } });
                mirror.Requests.Clear();
                mirror.DropFiltersAs = null;

                officialHandler.Reset();
                clientId.Value = "1234";
                clientSecret.Value = "s3cret";
                config.SetValue(JukeBoxSetting.SearchApi, SearchApi.Mirror);
            });

            AddStep("create listing", () =>
            {
                engine = new BeatmapSearchEngine();
                listing = new FullscreenListingOverlay(engine) { RelativeSizeAxes = Axes.Both };
                Child = new Container { RelativeSizeAxes = Axes.Both, Children = new Drawable[] { engine, listing } };
            });

            AddStep("show listing", () => listing.ShowSearch());
        }

        // ---- Backend selection -----------------------------------------------------------------

        [Test]
        public void MirrorIsTheDefaultAndKeepsWorking()
        {
            AddAssert("mirror selected", () => engine.Api.Value == SearchApi.Mirror);

            AddStep("search", () => engine.Query.Value = "camellia");
            AddUntilStep("mirror answered", () => engine.LoadedSets.Any());

            AddAssert("results are the mirror's", () => engine.LoadedSets[0].Title == "From Mirror");
            AddAssert("official never asked", () => officialHandler.SearchRequests == 0);
            AddAssert("not marked official", () => !engine.LoadedFromOfficial);
        }

        [Test]
        public void OfficialBackendAnswersWhenSelected()
        {
            AddStep("select official", () => engine.Api.Value = SearchApi.Official);
            AddStep("search", () => engine.Query.Value = "camellia");

            AddUntilStep("official answered", () => engine.LoadedSets.Any() && engine.LoadedFromOfficial);

            AddAssert("results are the official ones", () => engine.LoadedSets[0].Title == "From Official");
            AddAssert("mirror never asked", () => mirror.Requests.Count == 0);
            AddAssert("real total surfaced", () => engine.TotalResults == 2);
            AddAssert("status names the total", () => engine.Status.Value.Contains("2 results"));
        }

        [Test]
        public void SwitchingBackendLiveReRunsTheSearch()
        {
            AddStep("search on the mirror", () => engine.Query.Value = "camellia");
            AddUntilStep("mirror answered", () => engine.LoadedSets.Any());

            AddStep("switch to official", () => engine.Api.Value = SearchApi.Official);

            AddUntilStep("official answered", () => engine.LoadedFromOfficial);
            AddAssert("results replaced", () => engine.LoadedSets[0].Title == "From Official");
            AddAssert("query carried over", () => officialHandler.LastSearchUrl!.Contains("q=camellia"));
        }

        // ---- Paging ----------------------------------------------------------------------------

        [Test]
        public void OfficialPagesByCursor()
        {
            AddStep("select official", () => engine.Api.Value = SearchApi.Official);
            // Consumed as it is served, so the second page is the last one and the scene settles.
            AddStep("serve one cursor", () => officialHandler.NextCursor = "cur-1");
            AddStep("search", () => engine.Query.Value = "camellia");

            AddUntilStep("both pages in", () => engine.LoadedSets.Count == 4);

            AddAssert("cursor drove the second request", () => officialHandler.LastSearchUrl!.Contains("cursor_string=cur-1"));
            // A null cursor means "no more" exactly, where the mirror path can only infer it from a
            // page arriving short.
            AddAssert("end of results is exact", () => !engine.HasMore);
        }

        // Observed live against osu.ppy.sh: a deeper page can come back carrying Elasticsearch's
        // capped estimate (10,000) rather than the count page one reported, which made the status
        // line drop from "56,325 results" to "10,000 results" partway down an infinite scroll. The
        // total belongs to the search, not to the page.
        [Test]
        public void TotalIsTakenFromTheFirstPageAndNotOverwrittenWhilePaging()
        {
            AddStep("select official", () => engine.Api.Value = SearchApi.Official);
            AddStep("serve a real total then a capped one", () =>
            {
                officialHandler.FirstPageTotal = 56325;
                officialHandler.DeepPageTotal = 10000;
                officialHandler.NextCursor = "cur-1";
            });
            AddStep("search", () => engine.Query.Value = "camellia");

            AddUntilStep("both pages in", () => engine.LoadedSets.Count == 4);

            AddAssert("total is still page one's", () => engine.TotalResults == 56325);
            AddAssert("status still names it", () => engine.Status.Value.Contains("56,325 results"));

            // A new search must of course pick up its own total.
            AddStep("serve a different total", () => officialHandler.FirstPageTotal = 42);
            AddStep("search again", () => engine.Query.Value = "different");
            AddUntilStep("fresh total adopted", () => engine.TotalResults == 42);
        }

        // The exact user report: "i try change search api to mirror but it still use and show
        // official". Asserts the WIRE, not just the row set — the next request must actually go to
        // the mirror, carrying the filters the mirror rows are set to.
        [Test]
        public void SwitchingOfficialToMirrorSendsTheNextRequestToTheMirror()
        {
            AddStep("start on Official", () => config.SetValue(JukeBoxSetting.SearchApi, SearchApi.Official));
            AddStep("search", () => engine.Query.Value = "camellia");
            AddUntilStep("official answered", () => engine.LoadedFromOfficial);

            AddStep("set filters the mirror supports", () =>
            {
                engine.Mode.Value = "m";
                engine.Category.Value = "loved";
            });
            // Wait for the filter change's own debounced request to actually land, so the count
            // snapshot below isn't taken with searches still queued.
            AddUntilStep("official re-queried with the filters", () => officialHandler.LastSearchUrl!.Contains("s=loved"));
            AddWaitStep("let the debounce settle", 10);

            int officialRequestsBefore = 0;
            AddStep("note the official request count", () => officialRequestsBefore = officialHandler.SearchRequests);

            AddStep("switch the SETTING to Mirror", () => config.SetValue(JukeBoxSetting.SearchApi, SearchApi.Mirror));

            AddUntilStep("the mirror answered", () => engine.LoadedSets.Any() && !engine.LoadedFromOfficial);
            AddAssert("results are the mirror's", () => engine.LoadedSets[0].Title == "From Mirror");
            AddAssert("official was not asked again", () => officialHandler.SearchRequests == officialRequestsBefore);

            // The filters must travel on that mirror request — the whole point of the backend switch.
            AddAssert("mirror request carried the mode", () => mirror.Requests.Last().Mode == "m");
            AddAssert("mirror request carried the status", () => mirror.Requests.Last().Status == "loved");

            AddAssert("mirror row set restored", () => listing.GenreRow.Alpha == 0 && listing.LanguageRow.Alpha == 0);
        }

        // A slow official response landing after the user already switched must not repaint the
        // listing with official results — which is what "it still shows official" would look like
        // even once the setting itself is honoured.
        [Test]
        public void OfficialResponseArrivingAfterASwitchIsDiscarded()
        {
            var gate = new TaskCompletionSource<bool>();

            AddStep("hold the official response in flight", () =>
            {
                officialHandler.Gate = gate;
                config.SetValue(JukeBoxSetting.SearchApi, SearchApi.Official);
            });
            AddStep("search", () => engine.Query.Value = "camellia");
            AddUntilStep("official request is in flight", () => officialHandler.SearchRequests > 0);

            AddStep("switch to Mirror mid-flight", () =>
            {
                officialHandler.Gate = null;
                config.SetValue(JukeBoxSetting.SearchApi, SearchApi.Mirror);
            });
            AddUntilStep("mirror answered", () => engine.LoadedSets.Any() && !engine.LoadedFromOfficial);

            AddStep("release the stale official response", () => gate.SetResult(true));
            AddWaitStep("let it land", 10);

            AddAssert("still the mirror's results", () => engine.LoadedSets[0].Title == "From Mirror");
            AddAssert("still not marked official", () => !engine.LoadedFromOfficial);
        }

        // ---- Fallback ---------------------------------------------------------------------------

        [Test]
        public void RejectedCredentialsFallBackToTheMirror()
        {
            AddStep("select official", () => engine.Api.Value = SearchApi.Official);
            AddStep("reject the credentials", () => officialHandler.RejectCredentials());
            AddStep("search", () => engine.Query.Value = "camellia");

            AddUntilStep("mirror served instead", () => engine.LoadedSets.Any());

            AddAssert("results are the mirror's", () => engine.LoadedSets[0].Title == "From Mirror");
            AddAssert("not marked official", () => !engine.LoadedFromOfficial);
            AddAssert("reason surfaced in the listing", () => engine.Status.Value.Contains("credentials"));
            AddAssert("reason raised for a toast", () => engine.LastError.Value != null && engine.LastError.Value.Contains("credentials"));
        }

        [Test]
        public void MissingCredentialsFallBackToTheMirror()
        {
            AddStep("select official", () => engine.Api.Value = SearchApi.Official);
            AddStep("clear the credentials", () =>
            {
                clientId.Value = string.Empty;
                clientSecret.Value = string.Empty;
            });
            AddStep("search", () => engine.Query.Value = "camellia");

            AddUntilStep("mirror served instead", () => engine.LoadedSets.Any());

            AddAssert("results are the mirror's", () => engine.LoadedSets[0].Title == "From Mirror");
            AddAssert("nothing was sent", () => officialHandler.SearchRequests == 0);
            AddAssert("reason surfaced", () => engine.Status.Value.Contains("client id"));
        }

        [Test]
        public void FallenBackResultsStillGetTheClientSideGenreFilter()
        {
            AddStep("select official", () => engine.Api.Value = SearchApi.Official);
            AddStep("reject the credentials", () => officialHandler.RejectCredentials());
            AddStep("search", () => engine.Query.Value = "camellia");
            AddUntilStep("mirror served instead", () => engine.LoadedSets.Any());

            // The setting still says Official, but these results came from a mirror — which cannot
            // filter by genre, so the client-side sieve has to apply to them anyway.
            AddStep("filter to a genre the set isn't", () => engine.GenreId.Value = 5);
            AddUntilStep("set filtered out", () => !engine.VisibleSets.Any());
        }

        // The regression this pairs with: with a mirror that silently ignores every filter, results
        // never changed and nothing said why — indistinguishable from the filters being broken.
        [Test]
        public void UnfilterableMirrorResultsSaySoInTheListing()
        {
            AddStep("mirror cannot apply the filters", () => mirror.DropFiltersAs = "osu.direct");
            AddStep("search", () => engine.Query.Value = "camellia");

            AddUntilStep("results arrive anyway", () => engine.LoadedSets.Any());

            AddAssert("the mirror is named", () => engine.FiltersDroppedBy.Value == "osu.direct");
            AddAssert("status says the results are unfiltered", () => engine.Status.Value.Contains("can't apply these filters"));

            AddStep("mirror can apply them again", () => mirror.DropFiltersAs = null);
            AddStep("search again", () => engine.Query.Value = "camellia 2");
            AddUntilStep("warning cleared", () => engine.FiltersDroppedBy.Value == null && !engine.Status.Value.Contains("can't apply"));
        }

        // ---- Debounce ---------------------------------------------------------------------------

        [Test]
        public void DebounceIsLongerOnTheOfficialBackend()
        {
            AddAssert("mirror debounce", () => engine.DebounceMs == 300);
            AddStep("select official", () => engine.Api.Value = SearchApi.Official);
            AddAssert("official debounce respects the ~1 req/s policy", () => engine.DebounceMs >= 500);
        }

        // ---- Filter rows -------------------------------------------------------------------------

        // REPRODUCTION: the real user action is changing the dropdown in Settings, which writes the
        // CONFIG value — not the engine bindable the other tests poke directly.
        [Test]
        public void ChangingTheSettingUpdatesTheRowsLive()
        {
            AddAssert("starts hidden on mirror", () => listing.GenreRow.Alpha == 0);

            AddStep("switch the SETTING to Official", () => config.SetValue(JukeBoxSetting.SearchApi, SearchApi.Official));

            AddAssert("engine followed the setting", () => engine.Api.Value == SearchApi.Official);
            AddAssert("genre row visible", () => listing.GenreRow.Alpha == 1);
            AddAssert("language row visible", () => listing.LanguageRow.Alpha == 1);

            AddStep("switch the SETTING back to Mirror", () => config.SetValue(JukeBoxSetting.SearchApi, SearchApi.Mirror));
            AddAssert("genre row hidden again", () => listing.GenreRow.Alpha == 0);
        }

        // REPRODUCTION: the app starts with the setting ALREADY on Official (that is how a returning
        // user launches), so the listing is constructed against a backend it never saw *change*.
        // Every other test in this file flips the setting on a listing built while Mirror was
        // selected, which is a different code path.
        [Test]
        public void OfficialFromTheStartShowsGenreAndLanguage()
        {
            AddStep("persist Official, then build the listing", () =>
            {
                config.SetValue(JukeBoxSetting.SearchApi, SearchApi.Official);

                engine = new BeatmapSearchEngine();
                listing = new FullscreenListingOverlay(engine) { RelativeSizeAxes = Axes.Both };
                Child = new Container { RelativeSizeAxes = Axes.Both, Children = new Drawable[] { engine, listing } };
            });
            AddStep("show", () => listing.ShowSearch());

            AddAssert("engine adopted the persisted backend", () => engine.Api.Value == SearchApi.Official);
            AddAssert("genre row visible", () => listing.GenreRow.Alpha == 1);
            AddAssert("language row visible", () => listing.LanguageRow.Alpha == 1);
        }

        [Test]
        public void FilterRowsFollowTheActiveBackend()
        {
            AddAssert("mirror hides genre", () => listing.GenreRow.Alpha == 0);
            AddAssert("mirror hides language", () => listing.LanguageRow.Alpha == 0);
            AddAssert("rows the mirrors do support stay", () => listing.RulesetRow.Alpha == 1
                                                               && listing.CategoryRow.Alpha == 1
                                                               && listing.ExtraRow.Alpha == 1
                                                               && listing.MinStarsSlider.Alpha == 1);

            AddStep("select official", () => engine.Api.Value = SearchApi.Official);

            AddAssert("official shows genre", () => listing.GenreRow.Alpha == 1);
            AddAssert("official shows language", () => listing.LanguageRow.Alpha == 1);

            AddStep("back to the mirror", () => engine.Api.Value = SearchApi.Mirror);
            AddAssert("genre hidden again", () => listing.GenreRow.Alpha == 0);
        }

        [Test]
        public void RelevanceSortIsOfficialOnly()
        {
            AddStep("type a query", () => engine.Query.Value = "camellia");
            AddAssert("mirror strip has no relevance", () => engine.SortKey.Value != "relevance");

            AddStep("select official", () => engine.Api.Value = SearchApi.Official);

            // osu-web's own behaviour: a query makes relevance both available and the default.
            AddUntilStep("official sorts by relevance", () => engine.SortKey.Value == "relevance");

            AddStep("back to the mirror", () => engine.Api.Value = SearchApi.Mirror);
            AddUntilStep("relevance dropped", () => engine.SortKey.Value != "relevance");
        }

        [Test]
        public void GenreIsAServerParameterOnOfficialAndAClientFilterOnTheMirror()
        {
            AddStep("search on the mirror", () => engine.Query.Value = "camellia");
            AddUntilStep("mirror answered", () => mirror.Requests.Count == 1);

            AddStep("change genre", () => engine.GenreId.Value = 3);
            AddWaitStep("let any debounce elapse", 10);
            AddAssert("no new mirror request", () => mirror.Requests.Count == 1);

            AddStep("select official", () => engine.Api.Value = SearchApi.Official);
            AddUntilStep("official answered", () => engine.LoadedFromOfficial);
            AddAssert("genre travelled as a parameter", () => officialHandler.LastSearchUrl!.Contains("g=3"));

            AddStep("change genre again", () => engine.GenreId.Value = 5);
            AddUntilStep("genre re-queried server-side", () => officialHandler.LastSearchUrl!.Contains("g=5"));
        }

        // ---- Stubs ---------------------------------------------------------------------------------

        private class StubMirror : IBeatmapMirror
        {
            public string Name => "stub";

            public List<BeatmapSetInfo> Sets { get; } = new List<BeatmapSetInfo>();

            public List<SearchRequest> Requests { get; } = new List<SearchRequest>();

            /// <summary>When set, answers like a mirror that could not express the filters — which
            /// is what MirrorChain reports once no capable mirror could be reached.</summary>
            public string? DropFiltersAs;

            public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
            {
                Requests.Add(request);

                if (DropFiltersAs != null)
                    request.OnFiltersDropped?.Invoke(DropFiltersAs);

                return Task.FromResult(new List<BeatmapSetInfo>(Sets));
            }

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
                => throw new NotSupportedException("not exercised by this test scene");
        }

        /// <summary>
        /// Stands in for osu.ppy.sh: mints a token unless <see cref="TokenStatus"/> says otherwise,
        /// and answers searches with two sets and whatever <see cref="NextCursor"/> currently holds.
        /// </summary>
        private class StubOfficialHandler : HttpMessageHandler
        {
            public HttpStatusCode TokenStatus = HttpStatusCode.OK;
            public HttpStatusCode SearchStatus = HttpStatusCode.OK;

            /// <summary>When set, searches block on this until the test releases it — the only way
            /// to have an official response still in flight at the moment the backend changes.</summary>
            public TaskCompletionSource<bool>? Gate;
            public string? NextCursor;
            public int SearchRequests;
            public string? LastSearchUrl;

            /// <summary>The `total` served for a first page and for a cursor-carrying (deeper) one
            /// — split so a test can reproduce osu!'s real habit of answering a deeper page with
            /// Elasticsearch's capped estimate instead of the count it gave on page one.</summary>
            public int FirstPageTotal = 2;

            public int DeepPageTotal = 2;

            /// <summary>Rejects both the token exchange AND the search — a token already cached from
            /// an earlier test in this scene would otherwise sail past a token-only rejection, which
            /// is exactly what caching it is for.</summary>
            public void RejectCredentials()
            {
                TokenStatus = HttpStatusCode.Unauthorized;
                SearchStatus = HttpStatusCode.Unauthorized;
            }

            public void Reset()
            {
                TokenStatus = HttpStatusCode.OK;
                SearchStatus = HttpStatusCode.OK;
                Gate = null;
                NextCursor = null;
                SearchRequests = 0;
                LastSearchUrl = null;
                FirstPageTotal = 2;
                DeepPageTotal = 2;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                string url = request.RequestUri!.ToString();

                if (url.Contains("/oauth/token"))
                {
                    return Task.FromResult(new HttpResponseMessage(TokenStatus)
                    {
                        Content = new StringContent("{\"access_token\":\"tok\",\"expires_in\":86400}"),
                    });
                }

                SearchRequests++;
                LastSearchUrl = url;

                if (Gate != null)
                    return respondAfterGate();

                return respond();
            }

            private async Task<HttpResponseMessage> respondAfterGate()
            {
                await Gate!.Task.ConfigureAwait(false);
                return await respond().ConfigureAwait(false);
            }

            private Task<HttpResponseMessage> respond()
            {
                string url = LastSearchUrl!;

                if (SearchStatus != HttpStatusCode.OK)
                    return Task.FromResult(new HttpResponseMessage(SearchStatus) { Content = new StringContent("{}") });

                string cursor = NextCursor == null ? "null" : $"\"{NextCursor}\"";
                NextCursor = null;
                // Keyed on the request carrying a cursor, not on a request counter: that is what
                // actually distinguishes a first page from a deeper one, and it stays right across
                // several searches in one test.
                int total = url.Contains("cursor_string=") ? DeepPageTotal : FirstPageTotal;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"beatmapsets\":["
                        + "{\"id\":1,\"title\":\"From Official\",\"artist\":\"a\",\"creator\":\"c\",\"genre_id\":3,\"beatmaps\":[]},"
                        + "{\"id\":2,\"title\":\"From Official 2\",\"artist\":\"a\",\"creator\":\"c\",\"genre_id\":3,\"beatmaps\":[]}"
                        + $"],\"total\":{total},\"cursor_string\":{cursor}}}"),
                });
            }
        }
    }
}
