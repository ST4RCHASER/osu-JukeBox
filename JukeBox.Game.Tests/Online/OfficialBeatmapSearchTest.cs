#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using NUnit.Framework;
using osu.Framework.Bindables;

namespace JukeBox.Game.Tests.Online
{
    /// <summary>
    /// Covers <see cref="OfficialBeatmapSearch"/>: the client-credentials token exchange, the
    /// parameter encoding (which must match lazer's own <c>SearchBeatmapSetsRequest</c> exactly, or
    /// the same filter selection silently means something different here than in the game), cursor
    /// paging, and the failure modes <see cref="BeatmapSearchEngine"/> falls back on. The endpoint
    /// itself is effectively undocumented upstream, so these encodings are pinned here rather than
    /// trusted to stay obvious.
    /// </summary>
    [TestFixture]
    public class OfficialBeatmapSearchTest
    {
        private const string token_endpoint = "https://stub.invalid/oauth/token";
        private const string search_endpoint = "https://stub.invalid/api/v2/beatmapsets/search";
        private const string beatmap_endpoint = "https://stub.invalid/api/v2/beatmaps/";

        /// <summary>
        /// Records every request and replies from a queue of canned responses (the last one repeats),
        /// so a test can assert what was SENT — which for the token exchange is the whole point,
        /// since its body is the one place credentials travel.
        /// </summary>
        private class RecordingHandler : HttpMessageHandler
        {
            public readonly List<HttpRequestMessage> Requests = new List<HttpRequestMessage>();
            public readonly List<string> Bodies = new List<string>();
            public readonly Queue<HttpResponseMessage> Responses = new Queue<HttpResponseMessage>();

            private HttpResponseMessage? lastResponse;

            public void Enqueue(HttpStatusCode status, string body)
                => Responses.Enqueue(new HttpResponseMessage(status) { Content = new StringContent(body) });

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Requests.Add(request);
                Bodies.Add(request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

                if (Responses.Count > 0)
                    lastResponse = Responses.Dequeue();

                var source = lastResponse ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };

                // Re-created per call: a single HttpResponseMessage's content stream can only be
                // consumed once, and the last canned response deliberately repeats.
                return new HttpResponseMessage(source.StatusCode)
                {
                    Content = new StringContent(await source.Content.ReadAsStringAsync(ct).ConfigureAwait(false)),
                };
            }
        }

        private const string token_response = "{\"access_token\":\"tok-123\",\"expires_in\":86400,\"token_type\":\"Bearer\"}";

        private static string searchResponse(string cursor = "null", int total = 3, string sets = "[]")
            => $"{{\"beatmapsets\":{sets},\"total\":{total},\"cursor_string\":{cursor}}}";

        private static OfficialBeatmapSearch create(RecordingHandler handler, string id = "1234", string secret = "s3cret")
            => new OfficialBeatmapSearch(new HttpClient(handler),
                new Bindable<string>(id), new Bindable<string>(secret),
                token_endpoint, search_endpoint, beatmap_endpoint);

        // ---- Beatmap id -> set id --------------------------------------------------------------
        //
        // The ONLY route from a difficulty to the set it belongs to anywhere in this app: no mirror
        // offers a beatmap-id endpoint, which is why a /b/ link used to be refused outright.

        [Test]
        public async Task ABeatmapIdResolvesToItsSet()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(HttpStatusCode.OK, token_response);
            handler.Enqueue(HttpStatusCode.OK, "{\"id\":67890,\"beatmapset_id\":12345,\"version\":\"Insane\"}");

            int? setId = await create(handler).ResolveBeatmapSetIdAsync(67890);

            Assert.That(setId, Is.EqualTo(12345));
            Assert.That(handler.Requests[1].RequestUri!.ToString(), Is.EqualTo(beatmap_endpoint + "67890"));
            Assert.That(handler.Requests[1].Headers.GetValues("Authorization").Single(), Is.EqualTo("Bearer tok-123"));
            Assert.That(handler.Requests[1].Headers.GetValues("x-api-version").Single(), Is.EqualTo(OfficialBeatmapSearch.API_VERSION));
        }

        // osu! simply not having the beatmap is an ordinary answer, not a failure to shout about —
        // the caller words it.
        [Test]
        public async Task AnUnknownBeatmapResolvesToNothingRatherThanThrowing()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(HttpStatusCode.OK, token_response);
            handler.Enqueue(HttpStatusCode.NotFound, "{}");

            Assert.That(await create(handler).ResolveBeatmapSetIdAsync(1), Is.Null);
        }

        [Test]
        public async Task AResponseWithoutASetIdResolvesToNothing()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(HttpStatusCode.OK, token_response);
            handler.Enqueue(HttpStatusCode.OK, "{\"id\":67890}");

            Assert.That(await create(handler).ResolveBeatmapSetIdAsync(67890), Is.Null);
        }

        // Without credentials the request cannot succeed, so it is never sent — and the message
        // says the credentials are missing rather than implying the beatmap is.
        [Test]
        public void ResolvingWithoutCredentialsSaysSoAndSendsNothing()
        {
            var handler = new RecordingHandler();

            Assert.That(async () => await create(handler, id: string.Empty, secret: string.Empty).ResolveBeatmapSetIdAsync(1),
                Throws.InstanceOf<OfficialSearchException>().With.Message.Contains("client id/secret"));

            Assert.That(handler.Requests, Is.Empty);
        }

        // Same one-shot retry the search path gets: a token can be revoked between minting and use.
        [Test]
        public async Task ARevokedTokenIsRemintedOnceForTheBeatmapLookup()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(HttpStatusCode.OK, token_response);
            handler.Enqueue(HttpStatusCode.Unauthorized, "{}");
            handler.Enqueue(HttpStatusCode.OK, token_response);
            handler.Enqueue(HttpStatusCode.OK, "{\"beatmapset_id\":999}");

            Assert.That(await create(handler).ResolveBeatmapSetIdAsync(5), Is.EqualTo(999));
            Assert.That(handler.Requests.Count, Is.EqualTo(4), "token, rejected lookup, fresh token, lookup");
        }

        [Test]
        public void ACredentialRejectionAfterTheRetryIsReported()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(HttpStatusCode.OK, token_response);
            handler.Enqueue(HttpStatusCode.Unauthorized, "{}");
            handler.Enqueue(HttpStatusCode.OK, token_response);
            handler.Enqueue(HttpStatusCode.Unauthorized, "{}");

            Assert.That(async () => await create(handler).ResolveBeatmapSetIdAsync(5),
                Throws.InstanceOf<OfficialSearchException>().With.Message.Contains("rejected the credentials"));
        }

        [Test]
        public void ARateLimitedBeatmapLookupSaysToTryAgain()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(HttpStatusCode.OK, token_response);
            handler.Enqueue(HttpStatusCode.TooManyRequests, "{}");

            Assert.That(async () => await create(handler).ResolveBeatmapSetIdAsync(5),
                Throws.InstanceOf<OfficialSearchException>().With.Message.Contains("rate limit"));
        }

        // ---- Token ---------------------------------------------------------------------------

        [Test]
        public async Task TokenRequestUsesClientCredentialsGrant()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(HttpStatusCode.OK, token_response);
            handler.Enqueue(HttpStatusCode.OK, searchResponse());

            await create(handler).SearchAsync(new SearchRequest());

            Assert.That(handler.Requests[0].Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(handler.Requests[0].RequestUri!.ToString(), Is.EqualTo(token_endpoint));

            string body = handler.Bodies[0];
            Assert.That(body, Does.Contain("client_id=1234"));
            Assert.That(body, Does.Contain("client_secret=s3cret"));
            Assert.That(body, Does.Contain("grant_type=client_credentials"));
            Assert.That(body, Does.Contain("scope=public"));
        }

        [Test]
        public async Task SearchSendsBearerTokenAndPinnedApiVersion()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(HttpStatusCode.OK, token_response);
            handler.Enqueue(HttpStatusCode.OK, searchResponse());

            await create(handler).SearchAsync(new SearchRequest());

            var search = handler.Requests[1];
            Assert.That(search.Headers.GetValues("Authorization"), Is.EqualTo(new[] { "Bearer tok-123" }));
            Assert.That(search.Headers.GetValues("x-api-version"), Is.EqualTo(new[] { OfficialBeatmapSearch.API_VERSION }));
        }

        [Test]
        public async Task TokenIsCachedAcrossSearches()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(HttpStatusCode.OK, token_response);
            handler.Enqueue(HttpStatusCode.OK, searchResponse());

            var search = create(handler);
            await search.SearchAsync(new SearchRequest());
            await search.SearchAsync(new SearchRequest());

            // One token exchange, two searches — a token is good for 24 hours and re-minting one
            // per keystroke would be exactly the abuse the rate policy calls out.
            Assert.That(handler.Requests.Count, Is.EqualTo(3));
            Assert.That(handler.Requests[2].RequestUri!.ToString(), Does.StartWith(search_endpoint));
        }

        [Test]
        public async Task ExpiredTokenIsRefreshedOnceAfter401()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(HttpStatusCode.OK, token_response);
            handler.Enqueue(HttpStatusCode.Unauthorized, "{}");
            handler.Enqueue(HttpStatusCode.OK, "{\"access_token\":\"tok-456\",\"expires_in\":86400}");
            handler.Enqueue(HttpStatusCode.OK, searchResponse());

            var result = await create(handler).SearchAsync(new SearchRequest());

            Assert.That(result.Total, Is.EqualTo(3));
            Assert.That(handler.Requests[3].Headers.GetValues("Authorization"), Is.EqualTo(new[] { "Bearer tok-456" }));
        }

        [Test]
        public void PersistentlyRejectedTokenReportsCredentialFailure()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(HttpStatusCode.OK, token_response);
            handler.Enqueue(HttpStatusCode.Unauthorized, "{}");
            handler.Enqueue(HttpStatusCode.OK, token_response);
            handler.Enqueue(HttpStatusCode.Unauthorized, "{}");

            var ex = Assert.ThrowsAsync<OfficialSearchException>(() => create(handler).SearchAsync(new SearchRequest()));
            Assert.That(ex!.Message, Does.Contain("credentials"));
        }

        [Test]
        public void MissingCredentialsFailWithoutAnyRequest()
        {
            var handler = new RecordingHandler();

            var ex = Assert.ThrowsAsync<OfficialSearchException>(() => create(handler, id: string.Empty).SearchAsync(new SearchRequest()));

            Assert.That(ex!.Message, Does.Contain("client id"));
            Assert.That(handler.Requests, Is.Empty);
        }

        [Test]
        public void RateLimitIsReportedAsSuch()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(HttpStatusCode.OK, token_response);
            handler.Enqueue(HttpStatusCode.TooManyRequests, "{}");

            var ex = Assert.ThrowsAsync<OfficialSearchException>(() => create(handler).SearchAsync(new SearchRequest()));
            Assert.That(ex!.Message, Does.Contain("rate limit"));
        }

        [Test]
        public void FailedTokenRequestNeverEchoesTheResponseBody()
        {
            var handler = new RecordingHandler();
            // A server that (wrongly) reflected the secret back must not get it onto the screen.
            handler.Enqueue(HttpStatusCode.BadRequest, "{\"error\":\"invalid_request\",\"hint\":\"s3cret\"}");

            var ex = Assert.ThrowsAsync<OfficialSearchException>(() => create(handler).SearchAsync(new SearchRequest()));
            Assert.That(ex!.Message, Does.Not.Contain("s3cret"));
        }

        // ---- Parameter encoding ---------------------------------------------------------------

        [Test]
        public void EncodesEveryFilterTheWayLazerDoes()
        {
            string url = OfficialBeatmapSearch.BuildSearchUrl(new SearchRequest
            {
                Query = "camellia",
                Mode = "m",
                Status = "loved",
                Sort = "plays_asc",
                Extra = SearchExtra.VideoAndStoryboard,
                GenreId = 9,
                LanguageId = 3,
            }, search_endpoint);

            Assert.That(url, Does.StartWith(search_endpoint + "?"));
            Assert.That(url, Does.Contain("q=camellia"));
            Assert.That(url, Does.Contain("m=3"));           // mania is ruleset 3, not the mirrors' "m"
            Assert.That(url, Does.Contain("s=loved"));
            Assert.That(url, Does.Contain("g=9"));           // HipHop — the enum skips 8
            Assert.That(url, Does.Contain("l=3"));           // Japanese, by declaration order
            Assert.That(url, Does.Contain("e=video.storyboard"));
            Assert.That(url, Does.Contain("sort=plays_asc"));
        }

        [TestCase("o", 0)]
        [TestCase("t", 1)]
        [TestCase("c", 2)]
        [TestCase("m", 3)]
        public void MapsEveryRulesetLetterToItsOnlineId(string letter, int expected)
            => Assert.That(OfficialBeatmapSearch.ModeInt(letter), Is.EqualTo(expected));

        [Test]
        public void OmitsModeAndGenreAndLanguageWhenUnset()
        {
            string url = OfficialBeatmapSearch.BuildSearchUrl(new SearchRequest { Query = "x" }, search_endpoint);

            Assert.That(url, Does.Not.Contain("m="));
            Assert.That(url, Does.Not.Contain("g="));
            Assert.That(url, Does.Not.Contain("l="));
            Assert.That(url, Does.Not.Contain("e="));
        }

        [Test]
        public void TranslatesTheMirrorsAnyStatusSpelling()
        {
            Assert.That(OfficialBeatmapSearch.StatusFor("all"), Is.EqualTo("any"));
            Assert.That(OfficialBeatmapSearch.StatusFor("graveyard"), Is.EqualTo("graveyard"));
            Assert.That(OfficialBeatmapSearch.BuildSearchUrl(new SearchRequest { Status = "all" }, search_endpoint), Does.Contain("s=any"));
        }

        [Test]
        public void StarRangeRidesInsideTheQueryAsSearchKeywords()
        {
            // The endpoint has no star parameter at all — osu-web parses these keywords out of `q`.
            Assert.That(OfficialBeatmapSearch.BuildQueryString(new SearchRequest { Query = "camellia", MinStars = 4.5, MaxStars = 6 }),
                Is.EqualTo("camellia stars>=4.5 stars<=6"));

            Assert.That(OfficialBeatmapSearch.BuildQueryString(new SearchRequest { MinStars = 7 }), Is.EqualTo("stars>=7"));
            Assert.That(OfficialBeatmapSearch.BuildQueryString(new SearchRequest { Query = "  x  " }), Is.EqualTo("x"));
            Assert.That(OfficialBeatmapSearch.BuildQueryString(new SearchRequest()), Is.Empty);
        }

        [Test]
        public void SendsNsfwExplicitlyBothWays()
        {
            // Never omitted: the default for a user-less token is "hide", so leaving it out would
            // quietly serve a different result set than the request asked for.
            Assert.That(OfficialBeatmapSearch.BuildSearchUrl(new SearchRequest { IncludeNsfw = true }, search_endpoint), Does.Contain("nsfw=true"));
            Assert.That(OfficialBeatmapSearch.BuildSearchUrl(new SearchRequest { IncludeNsfw = false }, search_endpoint), Does.Contain("nsfw=false"));
        }

        [Test]
        public void PagesByCursorStringRatherThanPageNumber()
        {
            string first = OfficialBeatmapSearch.BuildSearchUrl(new SearchRequest(), search_endpoint);
            Assert.That(first, Does.Not.Contain("cursor_string="));
            Assert.That(first, Does.Not.Contain("page="));

            string next = OfficialBeatmapSearch.BuildSearchUrl(new SearchRequest { Cursor = "eyJpZCI6MX0=" }, search_endpoint);
            Assert.That(next, Does.Contain("cursor_string=" + Uri.EscapeDataString("eyJpZCI6MX0=")));
        }

        [Test]
        public async Task CursorFromOnePageDrivesTheNext()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(HttpStatusCode.OK, token_response);
            handler.Enqueue(HttpStatusCode.OK, searchResponse(cursor: "\"cur-1\""));
            handler.Enqueue(HttpStatusCode.OK, searchResponse(cursor: "null"));

            var search = create(handler);

            var page1 = await search.SearchAsync(new SearchRequest());
            Assert.That(page1.CursorString, Is.EqualTo("cur-1"));

            var page2 = await search.SearchAsync(new SearchRequest { Cursor = page1.CursorString });
            Assert.That(handler.Requests[2].RequestUri!.ToString(), Does.Contain("cursor_string=cur-1"));

            // A null cursor is the exact end-of-results signal the mirror path can only guess at.
            Assert.That(page2.CursorString, Is.Null);
        }

        // ---- Response mapping ------------------------------------------------------------------

        [Test]
        public void MapsTheEnvelopeAndSetsOntoOurDto()
        {
            var result = OfficialBeatmapSearch.ParseResult(searchResponse(cursor: "\"cur-9\"", total: 58349, sets:
                "[{\"id\":41823,\"title\":\"Blue Zenith\",\"artist\":\"xi\",\"creator\":\"Asphyxia\","
                + "\"status\":\"ranked\",\"video\":false,\"storyboard\":true,\"bpm\":200.0,"
                + "\"play_count\":12,\"favourite_count\":3,\"genre_id\":10,\"language_id\":5,"
                + "\"nsfw\":false,\"preview_url\":\"//b.ppy.sh/preview/41823.mp3\","
                + "\"beatmaps\":[{\"id\":1,\"mode\":\"osu\",\"version\":\"FOUR DIMENSIONS\",\"difficulty_rating\":7.51}]}]"));

            Assert.That(result.Total, Is.EqualTo(58349));
            Assert.That(result.CursorString, Is.EqualTo("cur-9"));
            Assert.That(result.Sets, Has.Count.EqualTo(1));

            var set = result.Sets[0];
            Assert.That(set.Id, Is.EqualTo(41823));
            Assert.That(set.DisplayTitle, Is.EqualTo("Blue Zenith"));
            Assert.That(set.Storyboard, Is.True);
            Assert.That(set.Bpm, Is.EqualTo(200.0));
            Assert.That(set.PreviewUrl, Is.EqualTo("//b.ppy.sh/preview/41823.mp3"));
            Assert.That(set.Beatmaps, Has.Count.EqualTo(1));
            Assert.That(set.Beatmaps[0].DifficultyRating, Is.EqualTo(7.51));

            // The official API serves genre/language as flat ids where the mirrors nest them; the
            // shared accessors are what let one DTO read either shape.
            Assert.That(set.GenreIdOrNull, Is.EqualTo(10));
            Assert.That(set.LanguageIdOrNull, Is.EqualTo(5));
        }

        [Test]
        public void ParsesAnEmptyResultSetWithoutFailing()
        {
            var result = OfficialBeatmapSearch.ParseResult("{\"beatmapsets\":[],\"total\":0,\"cursor_string\":null}");

            Assert.That(result.Sets, Is.Empty);
            Assert.That(result.Total, Is.Zero);
            Assert.That(result.CursorString, Is.Null);
        }
    }
}
