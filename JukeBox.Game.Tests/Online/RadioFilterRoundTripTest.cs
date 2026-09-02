#nullable enable

using JukeBox.Game.Configuration;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using NUnit.Framework;
using osu.Game.Overlays.BeatmapListing;

// Both namespaces declare a SearchExtra; this file asserts on the one a request carries.
using SearchExtra = JukeBox.Game.Online.SearchExtra;

namespace JukeBox.Game.Tests.Online
{
    /// <summary>
    /// The radio's filter set, followed to the URL each backend actually sends — the same standard
    /// <see cref="FilterRoundTripTest"/> holds the listing's rows to, and for the same reason: the
    /// radio picks a song and plays it, so a filter that silently does nothing is invisible in a
    /// way a listing full of unfiltered cards at least is not.
    /// </summary>
    [TestFixture]
    public class RadioFilterRoundTripTest
    {
        /// <summary>A fully-specified station: one non-neutral value per filter dimension.</summary>
        private static RadioFilters everyFilterSet()
        {
            var filters = new RadioFilters();

            filters.Mode.Value = RadioRuleset.Mania;
            filters.Category.Value = SearchCategory.Loved;
            filters.Genre.Value = SearchGenre.VideoGame;
            filters.Language.Value = SearchLanguage.Japanese;
            filters.HasVideo.Value = true;
            filters.HasStoryboard.Value = true;
            filters.MinStars.Value = 4;
            filters.MaxStars.Value = 6;
            filters.FeaturedArtists.Value = true;

            return filters;
        }

        /// <param name="filters">The station to apply.</param>
        /// <param name="available">What the backend about to serve this can express.</param>
        /// <param name="sort">The radio's randomised sort, which is part of what makes consecutive
        /// picks differ rather than being a station filter at all. Defaults to a non-default value
        /// because that is the realistic shape; tests about what a LIMITED backend can serve pass
        /// the default, so the assertion is about the filters rather than about the sort.</param>
        private static SearchRequest requestFor(RadioFilters filters, SearchFilters available, string sort = "plays_desc")
        {
            // The shape RadioService builds before applying the filters: a random keyword and sort.
            var request = new SearchRequest { Query = "k", Sort = sort };

            filters.Apply(request, available);
            return request;
        }

        [Test]
        public void EveryRadioFilterReachesTheOfficialUrl()
        {
            string url = OfficialBeatmapSearch.BuildSearchUrl(requestFor(everyFilterSet(), SearchFilters.All));

            Assert.That(url, Does.Contain("m=3"));                 // mania
            Assert.That(url, Does.Contain("s=loved"));
            Assert.That(url, Does.Contain("g=2"));                 // SearchGenre.VideoGame
            Assert.That(url, Does.Contain("l=3"));                 // SearchLanguage.Japanese
            Assert.That(url, Does.Contain("e=video.storyboard"));
            Assert.That(url, Does.Contain("c=featured_artists"));
            Assert.That(System.Uri.UnescapeDataString(url), Does.Contain("stars>=4 stars<=6"));
        }

        [Test]
        public void TheMirrorGetsTheFiltersItCanExpressAndNoneOfTheOthers()
        {
            var request = requestFor(everyFilterSet(), SearchFilters.AllMirror);
            string url = NerinyanMirror.BuildSearchUrl(request);

            // A star range switches NeriNyan onto its base64 body wholesale — the legacy query
            // string can't express a range, so with one set EVERY filter travels inside the JSON
            // rather than as a parameter (see StarRangeReachesTheNerinyanB64Body).
            Assert.That(url, Does.Contain("b64="));

            string json = System.Text.Encoding.UTF8.GetString(
                System.Convert.FromBase64String(System.Uri.UnescapeDataString(url.Split("b64=")[1])));

            Assert.That(json, Does.Contain("\"m\":\"m\""));         // NeriNyan spells mania "m"
            Assert.That(json, Does.Contain("\"s\":\"loved\""));
            Assert.That(json, Does.Contain("\"extra\":\"video.storyboard\""));
            Assert.That(json, Does.Contain("\"min\":4"));
            Assert.That(json, Does.Contain("\"max\":6"));

            // Genre, language and featured artists are dropped rather than sent: NeriNyan would
            // ignore them, and merely REQUIRING them would make the whole request unservable and
            // push the chain onto a mirror that can express even less.
            Assert.That(request.GenreId, Is.Null);
            Assert.That(request.LanguageId, Is.Null);
            Assert.That(request.FeaturedArtistsOnly, Is.False);
            Assert.That(request.RequiredFilters & ~SearchFilters.AllMirror, Is.EqualTo(SearchFilters.None));
        }

        /// <summary>
        /// osu.direct answers a keyword and nothing else — including no STATUS, which is the one
        /// filter a fresh <see cref="SearchRequest"/> arrives already carrying ("ranked"). Leaving
        /// that in place is what would make a fully-neutral station unservable there.
        /// </summary>
        [Test]
        public void AKeywordOnlyBackendGetsARequestItCanActuallyServe()
        {
            // The default sort, so this is a statement about the STATION's filters. (The radio's
            // own randomised sort is a separate matter, and one osu.direct has never been able to
            // express either — RadioService asks its mirror directly rather than through the chain,
            // so that has always been a silently-ignored parameter rather than a refusal.)
            var request = requestFor(everyFilterSet(), SearchFilters.Keyword, sort: SearchRequest.DEFAULT_SORT);

            Assert.That(request.Status, Is.EqualTo(SearchRequest.ANY_STATUS));
            Assert.That(request.Mode, Is.Null);
            Assert.That(request.MinStars, Is.Null);
            Assert.That(request.MaxStars, Is.Null);
            Assert.That(request.Extra, Is.EqualTo(SearchExtra.None));

            IBeatmapMirror osuDirect = new OsuDirectMirror(new System.Net.Http.HttpClient());

            Assert.That(osuDirect.CanApplyFilters(request), Is.True);
        }

        /// <summary>
        /// A neutral station has to ask exactly what the radio asked before there were filters at
        /// all, or every existing user's radio quietly narrows on upgrade.
        /// </summary>
        [Test]
        public void ANeutralStationAsksTheSameBroadQuestionAsBefore()
        {
            var request = requestFor(new RadioFilters(), SearchFilters.All);

            Assert.That(request.Status, Is.EqualTo("ranked"));
            Assert.That(request.Mode, Is.Null);
            Assert.That(request.GenreId, Is.Null);
            Assert.That(request.LanguageId, Is.Null);
            Assert.That(request.Extra, Is.EqualTo(SearchExtra.None));
            Assert.That(request.MinStars, Is.Null);
            Assert.That(request.MaxStars, Is.Null);
            Assert.That(request.FeaturedArtistsOnly, Is.False);
        }

        /// <summary>
        /// The sliders are independent, so the user can cross them. An inverted range is a request
        /// osu-web reads as "impossible", where un-crossing it is what they plainly meant — same
        /// rule the listing's own star row follows.
        /// </summary>
        [Test]
        public void ACrossedStarPairIsUncrossedRatherThanSentInverted()
        {
            var filters = new RadioFilters();

            filters.MinStars.Value = 7;
            filters.MaxStars.Value = 3;

            var request = requestFor(filters, SearchFilters.All);

            Assert.That(request.MinStars, Is.EqualTo(3));
            Assert.That(request.MaxStars, Is.EqualTo(7));
        }

        /// <summary>
        /// The radio and the listing must never disagree about what a backend can do — a row the
        /// listing hides is a filter the radio must not send, and the shared
        /// <see cref="SearchCapability"/> is what guarantees it.
        /// </summary>
        [Test]
        public void TheRadioAndTheListingReadCapabilityFromTheSameSource()
        {
            IBeatmapMirror keywordOnly = new OsuDirectMirror(new System.Net.Http.HttpClient());

            Assert.That(SearchCapability.For(SearchApi.Official, keywordOnly), Is.EqualTo(SearchFilters.All));
            Assert.That(SearchCapability.For(SearchApi.Mirror, keywordOnly), Is.EqualTo(SearchFilters.Keyword));

            var engine = new BeatmapSearchEngine();
            var radio = new RadioService(keywordOnly, searchApi: new osu.Framework.Bindables.Bindable<SearchApi>(SearchApi.Mirror));

            // The engine's published offer is seeded from the same call, so the two agree by
            // construction rather than by two copies of the rule happening to match.
            engine.AvailableFilters.Value = SearchCapability.For(SearchApi.Mirror, keywordOnly);

            Assert.That(radio.AvailableFilters, Is.EqualTo(engine.AvailableFilters.Value));
        }
    }
}
