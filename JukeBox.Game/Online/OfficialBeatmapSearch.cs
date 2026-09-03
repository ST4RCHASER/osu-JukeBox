#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Bindables;

namespace JukeBox.Game.Online
{
    /// <summary>
    /// Search against osu!'s own API (<c>GET /api/v2/beatmapsets/search</c>) — the same endpoint
    /// osu!lazer and the website's beatmap listing use, so every filter our listing shows is
    /// answered server-side with exactly the game's semantics (genre and language become real
    /// parameters rather than the mirror path's client-side sieve, star ranges work on every query,
    /// and paging is exact rather than guessed from a short page).
    ///
    /// Deliberately a plain <see cref="HttpClient"/> rather than lazer's <c>APIAccess</c>: that type
    /// is a <c>CompositeComponent</c> built around a signed-in user (it wants an <c>OsuGameBase</c>,
    /// an <c>OsuConfigManager</c>, its own request thread and a notifications WebSocket, and has no
    /// client-credentials path at all), against which this is one GET with a bearer header. The
    /// pieces worth reusing from lazer — the filter enums and their wire encodings — we already
    /// reference directly in the listing UI.
    ///
    /// Authentication is the OAuth <c>client_credentials</c> grant: an app-only ("guest") token
    /// carrying the <c>public</c> scope, which is all this endpoint requires. That token is enough
    /// for the full filter set because osu-web only gates advanced search on a token EXISTING, not
    /// on it belonging to a user. It is not enough for the filters that read a user (played state,
    /// rank achieved, favourites, "mine") — those quietly return nothing app-only and are therefore
    /// not offered in the UI at all while this backend is active.
    /// </summary>
    public class OfficialBeatmapSearch
    {
        public const string TOKEN_ENDPOINT = "https://osu.ppy.sh/oauth/token";
        public const string SEARCH_ENDPOINT = "https://osu.ppy.sh/api/v2/beatmapsets/search";

        /// <summary>
        /// Looks up ONE difficulty by its beatmap id, which is the only way in this app to learn
        /// which set a difficulty belongs to: no mirror offers a beatmap-id endpoint (see
        /// <see cref="BeatmapLinkKind.Beatmap"/>), so a <c>/b/</c> link or a bare id that is not a
        /// set can only be resolved here. The id is appended to this base.
        /// </summary>
        public const string BEATMAP_ENDPOINT = "https://osu.ppy.sh/api/v2/beatmaps/";

        /// <summary>
        /// Pinned rather than derived from today's date (which is what lazer does for local builds):
        /// osu-web's <c>x-api-version</c> selects a response shape, so a moving value would let an
        /// upstream shape change reach us unannounced. Bump deliberately, with the parsing checked.
        /// </summary>
        public const string API_VERSION = "20260101";

        /// <summary>
        /// The endpoint's page size, fixed server-side — <c>BeatmapsetSearchRequestParams</c> accepts
        /// no <c>limit</c>/<c>size</c> parameter, so unlike the mirrors this cannot be asked for
        /// <see cref="BeatmapSearchEngine.PAGE_SIZE"/> results.
        /// </summary>
        public const int PAGE_SIZE = 50;

        /// <summary>The <c>c</c> value selecting osu-web's "Featured Artists" general filter — see
        /// where it is written in <see cref="BuildSearchUrl"/> for why it is not <c>general</c>.</summary>
        public const string FEATURED_ARTISTS_GENERAL = "featured_artists";

        private readonly HttpClient http;
        private readonly IBindable<string> clientId;
        private readonly IBindable<string> clientSecret;
        private readonly string tokenEndpoint;
        private readonly string searchEndpoint;
        private readonly string beatmapEndpoint;

        // Guards the cached token against a herd of concurrent searches each minting their own.
        private readonly SemaphoreSlim tokenLock = new SemaphoreSlim(1, 1);

        private string? cachedToken;
        private DateTimeOffset cachedTokenExpiry;

        // The credentials the cached token was minted with: comparing against the live bindables is
        // what makes an edit in settings take effect on the very next search, with no subscription
        // (and therefore no cross-thread bindable access) involved.
        private string cachedTokenCredentials = string.Empty;

        public OfficialBeatmapSearch(HttpClient http, IBindable<string> clientId, IBindable<string> clientSecret,
                                     string tokenEndpoint = TOKEN_ENDPOINT, string searchEndpoint = SEARCH_ENDPOINT,
                                     string beatmapEndpoint = BEATMAP_ENDPOINT)
        {
            this.http = http;
            this.clientId = clientId;
            this.clientSecret = clientSecret;
            this.tokenEndpoint = tokenEndpoint;
            this.searchEndpoint = searchEndpoint;
            this.beatmapEndpoint = beatmapEndpoint;
        }

        /// <summary>Whether both credentials are present — searching without them can only fail.</summary>
        public bool HasCredentials => clientId.Value.Trim().Length > 0 && clientSecret.Value.Trim().Length > 0;

        /// <summary>
        /// Runs one search. Throws <see cref="OfficialSearchException"/> with a message meant for the
        /// user on every foreseeable failure (no credentials, credentials rejected, rate limited,
        /// upstream error) — <see cref="BeatmapSearchEngine"/> shows that message and falls back to
        /// the mirror search rather than leaving the listing empty.
        /// </summary>
        public async Task<OfficialSearchResult> SearchAsync(SearchRequest request, CancellationToken ct = default)
        {
            if (!HasCredentials)
                throw new OfficialSearchException("osu! API client id/secret not set (Settings → Online)");

            string token = await getTokenAsync(ct).ConfigureAwait(false);

            using var response = await sendSearchAsync(request, token, ct).ConfigureAwait(false);

            // A token can be revoked (or simply expire early) between minting and use — drop the
            // cached one and mint a fresh one for exactly one retry before reporting a failure, so a
            // revoked token costs a single extra round trip rather than the whole session.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await invalidateTokenAsync(ct).ConfigureAwait(false);
                string retryToken = await getTokenAsync(ct).ConfigureAwait(false);

                using var retry = await sendSearchAsync(request, retryToken, ct).ConfigureAwait(false);

                if (retry.StatusCode == HttpStatusCode.Unauthorized)
                    throw new OfficialSearchException("osu! API rejected the credentials (check the client id/secret)");

                return await readResultAsync(retry, ct).ConfigureAwait(false);
            }

            return await readResultAsync(response, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// The beatmapset a single difficulty belongs to, or null when osu! does not know that
        /// beatmap. This is the ONLY route from a beatmap id to its set anywhere in the app — the
        /// mirrors expose a set filter and nothing else — so a <c>/b/</c> link or a bare id that
        /// turned out not to be a set is resolvable only when the user has configured credentials.
        ///
        /// <para>
        /// Throws <see cref="OfficialSearchException"/> with a user-facing message on the same
        /// foreseeable failures <see cref="SearchAsync"/> does, and for the same reason: the caller
        /// shows it. "No credentials" is one of those, deliberately — a user whose beatmap link did
        /// not resolve needs to be told the app cannot look one up rather than that their map does
        /// not exist.
        /// </para>
        /// </summary>
        public async Task<int?> ResolveBeatmapSetIdAsync(int beatmapId, CancellationToken ct = default)
        {
            if (!HasCredentials)
                throw new OfficialSearchException("osu! API client id/secret not set (Settings → Online)");

            string token = await getTokenAsync(ct).ConfigureAwait(false);

            using var response = await sendBeatmapAsync(beatmapId, token, ct).ConfigureAwait(false);

            // Same one-shot retry as the search path: a token can be revoked or expire early
            // between minting and use.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await invalidateTokenAsync(ct).ConfigureAwait(false);
                string retryToken = await getTokenAsync(ct).ConfigureAwait(false);

                using var retry = await sendBeatmapAsync(beatmapId, retryToken, ct).ConfigureAwait(false);

                if (retry.StatusCode == HttpStatusCode.Unauthorized)
                    throw new OfficialSearchException("osu! API rejected the credentials (check the client id/secret)");

                return await readBeatmapSetIdAsync(retry, ct).ConfigureAwait(false);
            }

            return await readBeatmapSetIdAsync(response, ct).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> sendBeatmapAsync(int beatmapId, string token, CancellationToken ct)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, beatmapEndpoint + beatmapId.ToString(CultureInfo.InvariantCulture));
            message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            message.Headers.TryAddWithoutValidation("Accept", "application/json");
            message.Headers.TryAddWithoutValidation("x-api-version", API_VERSION);

            return await http.SendAsync(message, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Pulls <c>beatmapset_id</c> out of a single-beatmap response. A 404 is not an error worth
        /// throwing over — "osu! has no such beatmap" is an ordinary answer, and the caller words it
        /// for the user.
        /// </summary>
        private static async Task<int?> readBeatmapSetIdAsync(HttpResponseMessage response, CancellationToken ct)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new OfficialSearchException("osu! API rate limit reached — try again in a moment");

            if (!response.IsSuccessStatusCode)
                throw new OfficialSearchException($"osu! API beatmap lookup failed ({(int)response.StatusCode})");

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            try
            {
                using var document = JsonDocument.Parse(json);

                if (document.RootElement.TryGetProperty("beatmapset_id", out var element) && element.TryGetInt32(out int setId) && setId > 0)
                    return setId;
            }
            catch (JsonException e)
            {
                throw new OfficialSearchException($"osu! API returned something unreadable ({e.Message})");
            }

            return null;
        }

        private async Task<HttpResponseMessage> sendSearchAsync(SearchRequest request, string token, CancellationToken ct)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, BuildSearchUrl(request, searchEndpoint));
            message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            message.Headers.TryAddWithoutValidation("Accept", "application/json");
            message.Headers.TryAddWithoutValidation("x-api-version", API_VERSION);

            return await http.SendAsync(message, ct).ConfigureAwait(false);
        }

        private static async Task<OfficialSearchResult> readResultAsync(HttpResponseMessage response, CancellationToken ct)
        {
            // 429 is called out separately because it is the one failure the user can act on by
            // simply waiting — osu!'s stated policy is ~1 request/second (see the engine's debounce).
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new OfficialSearchException("osu! API rate limit reached — try again in a moment");

            if (!response.IsSuccessStatusCode)
                throw new OfficialSearchException($"osu! API search failed ({(int)response.StatusCode})");

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return ParseResult(json);
        }

        /// <summary>
        /// Splits the search envelope (<c>beatmapsets</c>, <c>total</c>, <c>cursor_string</c>) apart
        /// and hands the set array to the same <see cref="BeatmapSetInfo.ParseList"/> the mirrors use
        /// — the official field names are the schema our DTO was already modelled on.
        /// </summary>
        internal static OfficialSearchResult ParseResult(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var result = new OfficialSearchResult();

            if (root.TryGetProperty("beatmapsets", out var sets) && sets.ValueKind == JsonValueKind.Array)
                result.Sets = BeatmapSetInfo.ParseList(sets.GetRawText());

            if (root.TryGetProperty("total", out var total) && total.TryGetInt32(out int totalValue))
                result.Total = totalValue;

            // Null (not absent) on the last page — that is precisely the end-of-results signal, and
            // the reason paging here is exact where the mirror path has to guess from a short page.
            if (root.TryGetProperty("cursor_string", out var cursor) && cursor.ValueKind == JsonValueKind.String)
                result.CursorString = cursor.GetString();

            return result;
        }

        // ---- Request building ---------------------------------------------------------------

        /// <summary>
        /// Serialises <paramref name="r"/> exactly as lazer's own <c>SearchBeatmapSetsRequest</c>
        /// does, so a filter set means the same thing here as it does in the game. Notable
        /// encodings: the ruleset is an int (not the mirrors' letters), genre/language are the raw
        /// enum ints, and the star range has no parameter at all — it rides inside <c>q</c> as
        /// osu-web's <c>stars&gt;=</c>/<c>stars&lt;=</c> search keywords.
        /// </summary>
        internal static string BuildSearchUrl(SearchRequest r, string endpoint = SEARCH_ENDPOINT)
        {
            var query = new List<string>();

            string q = BuildQueryString(r);

            if (q.Length > 0)
                query.Add($"q={Uri.EscapeDataString(q)}");

            if (ModeInt(r.Mode) is int mode)
                query.Add($"m={mode}");

            query.Add($"s={StatusFor(r.Status)}");

            if (r.GenreId is int genre)
                query.Add($"g={genre}");

            if (r.LanguageId is int language)
                query.Add($"l={language}");

            string extra = r.Extra switch
            {
                SearchExtra.Storyboard => "storyboard",
                SearchExtra.Video => "video",
                SearchExtra.VideoAndStoryboard => "video.storyboard",
                _ => string.Empty,
            };

            if (extra.Length > 0)
                query.Add($"e={extra}");

            // osu-web's "General" row, whose parameter is `c` — NOT `general`, despite that being
            // the name the row carries everywhere else in the API surface. Verified live against
            // osu.ppy.sh: `general=featured_artists` (and `general[]=`, and a dot-joined list under
            // that name) is accepted and then ignored, returning byte-for-byte the unfiltered
            // result an unrecognised value returns, while `c=featured_artists` takes the total from
            // 1,247,030 to 99,171 with every returned set carrying a track_id. osu-web reads it as
            // `explode('.', $params['c'])`, so further generals would join onto this with dots.
            if (r.FeaturedArtistsOnly)
                query.Add($"c={FEATURED_ARTISTS_GENERAL}");

            query.Add($"sort={r.Sort}");

            // Always sent explicitly: with no user behind the token, osu-web's default is the
            // signed-out "hide" preference, so omitting this silently drops explicit sets rather
            // than honouring what the request actually asked for.
            query.Add($"nsfw={(r.IncludeNsfw ? "true" : "false")}");

            if (!string.IsNullOrEmpty(r.Cursor))
                query.Add($"cursor_string={Uri.EscapeDataString(r.Cursor)}");

            return $"{endpoint}?{string.Join("&", query)}";
        }

        /// <summary>
        /// The <c>q</c> value: the user's keywords plus the star range expressed in osu-web's own
        /// advanced-search keyword syntax, which is the only way the endpoint accepts a difficulty
        /// range (there is no <c>stars</c> parameter).
        /// </summary>
        internal static string BuildQueryString(SearchRequest r)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(r.Query))
                parts.Add(r.Query.Trim());

            if (r.MinStars is double min)
                parts.Add($"stars>={min.ToString("0.##", CultureInfo.InvariantCulture)}");

            if (r.MaxStars is double max)
                parts.Add($"stars<={max.ToString("0.##", CultureInfo.InvariantCulture)}");

            return string.Join(" ", parts);
        }

        /// <summary>The mirrors' single-letter ruleset filter as osu!'s ruleset id; null = any.</summary>
        internal static int? ModeInt(string? mode) => mode switch
        {
            "o" => 0,
            "t" => 1,
            "c" => 2,
            "m" => 3,
            _ => null,
        };

        /// <summary>
        /// osu-web spells "no status filter" <c>any</c> where the mirrors spell it <c>all</c>; every
        /// other status name is shared between the two vocabularies.
        /// </summary>
        internal static string StatusFor(string status) => status == "all" ? "any" : status;

        // ---- Token ----------------------------------------------------------------------------

        private async Task invalidateTokenAsync(CancellationToken ct)
        {
            await tokenLock.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                cachedToken = null;
            }
            finally
            {
                tokenLock.Release();
            }
        }

        /// <summary>
        /// The app-only (<c>client_credentials</c>) token, for another feature that speaks to the
        /// same API with the same credentials — spectating, which needs a bearer token for the
        /// public user and score endpoints and has no reason to mint a second one of its own.
        ///
        /// <para>
        /// Sharing this one keeps the caching, the serialisation and the expiry headroom in a single
        /// place: two independent minters against the same client id would each hold their own token
        /// and refresh on their own schedule, for no benefit.
        /// </para>
        /// </summary>
        public Task<string> AcquireTokenAsync(CancellationToken ct = default) => getTokenAsync(ct);

        private async Task<string> getTokenAsync(CancellationToken ct)
        {
            string id = clientId.Value.Trim();
            string secret = clientSecret.Value.Trim();
            string credentials = $"{id}\n{secret}";

            await tokenLock.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                // A minute of headroom: a token that expires while the request is in flight would
                // cost the 401 retry above for nothing.
                if (cachedToken != null && cachedTokenCredentials == credentials && DateTimeOffset.UtcNow < cachedTokenExpiry - TimeSpan.FromMinutes(1))
                    return cachedToken;

                var form = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", id),
                    new KeyValuePair<string, string>("client_secret", secret),
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string>("scope", "public"),
                });

                using var message = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint) { Content = form };
                message.Headers.TryAddWithoutValidation("Accept", "application/json");

                using var response = await http.SendAsync(message, ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    // Deliberately reports only the status: the response body of a failed token
                    // request is the one place the secret could plausibly be echoed back, and this
                    // message reaches both the log and the on-screen status line.
                    throw new OfficialSearchException(response.StatusCode == HttpStatusCode.Unauthorized
                        ? "osu! API rejected the credentials (check the client id/secret)"
                        : $"osu! API token request failed ({(int)response.StatusCode})");
                }

                string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                using var document = JsonDocument.Parse(json);

                if (!document.RootElement.TryGetProperty("access_token", out var accessToken) || accessToken.GetString() is not string value)
                    throw new OfficialSearchException("osu! API returned no access token");

                int expiresIn = document.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out int seconds)
                    ? seconds
                    : 86400;

                cachedToken = value;
                cachedTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
                cachedTokenCredentials = credentials;

                return value;
            }
            finally
            {
                tokenLock.Release();
            }
        }
    }

    /// <summary>One page of official search results.</summary>
    public class OfficialSearchResult
    {
        public List<BeatmapSetInfo> Sets = new List<BeatmapSetInfo>();

        /// <summary>
        /// Opaque marker for the NEXT page, or null once there are none — the endpoint pages by
        /// cursor, not by offset (offsets exist but run into Elasticsearch's result window).
        /// </summary>
        public string? CursorString;

        /// <summary>Total matches upstream, not just the ones on this page.</summary>
        public int Total;
    }

    /// <summary>
    /// A search failure whose <see cref="Exception.Message"/> is written for the user — it is shown
    /// verbatim in the listing's status line and as a toast, so it must never carry credentials or
    /// raw response bodies.
    /// </summary>
    public class OfficialSearchException : Exception
    {
        public OfficialSearchException(string message)
            : base(message)
        {
        }
    }
}
