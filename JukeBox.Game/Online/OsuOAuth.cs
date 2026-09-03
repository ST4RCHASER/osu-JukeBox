#nullable enable

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JukeBox.Game.Online
{
    /// <summary>
    /// Signing in to osu! as the USER, through osu!'s own login page.
    ///
    /// <para>
    /// This is the OAuth 2.0 AUTHORIZATION CODE flow against the user's OWN registered osu!
    /// application (the client id/secret already in Settings). The app never sees the password:
    /// the browser goes to osu.ppy.sh, the user signs in there, and osu! hands back a short-lived
    /// CODE on a loopback URL which is then exchanged for tokens.
    /// </para>
    ///
    /// <para>
    /// Deliberately NOT the password grant, and deliberately not osu!lazer's first-party client id.
    /// Both would work and both are wrong: the first asks the user to type their osu! password into
    /// a third-party app, and the second impersonates the official client to obtain authority far
    /// beyond anything this app needs. A user token from the user's own app, scoped to
    /// <see cref="DEFAULT_SCOPE"/>, is the whole requirement — it is what lets us download replays
    /// for spectating.
    /// </para>
    /// </summary>
    public class OsuOAuth
    {
        public const string AUTHORIZE_ENDPOINT = "https://osu.ppy.sh/oauth/authorize";
        public const string TOKEN_ENDPOINT = "https://osu.ppy.sh/oauth/token";
        public const string ME_ENDPOINT = "https://osu.ppy.sh/api/v2/me";

        /// <summary>
        /// Everything this app does with a user token is reading public data (resolving usernames,
        /// reading recent scores, downloading replays), which is exactly the <c>public</c> scope.
        /// Nothing here needs <c>identify</c> beyond the username shown in Settings — which
        /// <c>public</c> already covers on <c>/me</c> — and nothing needs a write scope at all.
        /// </summary>
        public const string DEFAULT_SCOPE = "public";

        /// <summary>
        /// The loopback port the callback listens on. FIXED rather than ephemeral, because
        /// <see cref="RedirectUri"/> uses the host name <c>localhost</c>.
        ///
        /// <para>
        /// The nuance is worth recording, because it is the opposite of what the RFC suggests.
        /// league/oauth2-server DOES implement RFC 8252 §7.3 — but its loopback test matches the IP
        /// literals <c>127.0.0.1</c> and <c>[::1]</c> ONLY, never the name <c>localhost</c>
        /// (RedirectUriValidator::isLoopbackUri). So a <c>127.0.0.1</c> redirect would be matched
        /// ignoring its port and could use any port at all, while a <c>localhost</c> one is matched
        /// as an exact string and must use the port the user registered.
        /// </para>
        ///
        /// <para>
        /// We stay on <c>localhost</c> with a fixed port anyway: users have already registered this
        /// exact URI on their osu! applications, and switching to the IP literal would silently
        /// break every one of them to buy port-independence nobody has asked for. If that ever
        /// becomes worth having, it is a redirect the user must re-register, not a free change.
        /// </para>
        /// </summary>
        public const int LOOPBACK_PORT = 7274;

        /// <summary>The exact string the user must register on their osu! OAuth application.</summary>
        public static string RedirectUri => $"http://localhost:{LOOPBACK_PORT}/callback";

        private readonly HttpClient http;
        private readonly string authorizeEndpoint;
        private readonly string tokenEndpoint;
        private readonly string meEndpoint;

        public OsuOAuth(HttpClient http,
                        string authorizeEndpoint = AUTHORIZE_ENDPOINT,
                        string tokenEndpoint = TOKEN_ENDPOINT,
                        string meEndpoint = ME_ENDPOINT)
        {
            this.http = http;
            this.authorizeEndpoint = authorizeEndpoint;
            this.tokenEndpoint = tokenEndpoint;
            this.meEndpoint = meEndpoint;
        }

        /// <summary>
        /// A random, unguessable value echoed back by osu! and checked on return. Without it, any
        /// page the user visits could hit our loopback URL with a code of its own choosing and
        /// bind this app to an attacker's account.
        /// </summary>
        public static string NewState() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        /// <summary>The URL to open in the user's browser — osu!'s own consent page.</summary>
        internal string BuildAuthorizeUrl(string clientId, string state, string scope = DEFAULT_SCOPE, string? redirectUri = null)
        {
            var query = new List<string>
            {
                $"client_id={Uri.EscapeDataString(clientId)}",
                $"redirect_uri={Uri.EscapeDataString(redirectUri ?? RedirectUri)}",
                "response_type=code",
                $"scope={Uri.EscapeDataString(scope)}",
                $"state={Uri.EscapeDataString(state)}",
            };

            return $"{authorizeEndpoint}?{string.Join("&", query)}";
        }

        /// <summary>Swaps the one-time code for an access/refresh pair.</summary>
        public Task<OsuTokenSet> ExchangeCodeAsync(string clientId, string clientSecret, string code,
                                                   string? redirectUri = null, CancellationToken ct = default)
            => postTokenAsync(new[]
            {
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("redirect_uri", redirectUri ?? RedirectUri),
            }, ct);

        /// <summary>
        /// Trades a refresh token for a fresh pair. osu! rotates the refresh token, so the RESULT's
        /// refresh token must replace the stored one — reusing the old one after a refresh fails.
        /// </summary>
        public Task<OsuTokenSet> RefreshAsync(string clientId, string clientSecret, string refreshToken,
                                              CancellationToken ct = default)
            => postTokenAsync(new[]
            {
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("refresh_token", refreshToken),
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("scope", DEFAULT_SCOPE),
            }, ct);

        private async Task<OsuTokenSet> postTokenAsync(KeyValuePair<string, string>[] form, CancellationToken ct)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint) { Content = new FormUrlEncodedContent(form) };
            message.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await http.SendAsync(message, ct).ConfigureAwait(false);

            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new OsuOAuthException(DescribeTokenError(body, (int)response.StatusCode));

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!root.TryGetProperty("access_token", out var access) || access.GetString() is not string accessToken)
                throw new OsuOAuthException("osu! returned no access token");

            root.TryGetProperty("refresh_token", out var refresh);
            int expiresIn = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out int seconds) ? seconds : 86400;

            return new OsuTokenSet(accessToken, refresh.ValueKind == JsonValueKind.String ? refresh.GetString()! : string.Empty,
                DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        }

        /// <summary>
        /// Turns osu!'s token-endpoint error body into something the user can act on.
        ///
        /// <para>
        /// <c>redirect_uri_mismatch</c> gets its own sentence because it is BY FAR the most likely
        /// failure and the least self-explanatory: it means the app's registered Redirect URI is
        /// blank or different, which is a two-minute fix on the osu! account page and an
        /// indefinite mystery without being told.
        /// </para>
        ///
        /// <para>
        /// Internal and pure so the mapping can be tested without a network, and deliberately never
        /// echoes the raw body: a failed token exchange is the one response that could contain the
        /// client secret we just posted.
        /// </para>
        /// </summary>
        internal static string DescribeTokenError(string body, int statusCode)
        {
            string? error = null;

            try
            {
                using var document = JsonDocument.Parse(body);

                if (document.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                    error = e.GetString();
            }
            catch (JsonException)
            {
                // A non-JSON body (a proxy error page, an HTML 502) says nothing useful and must
                // not be forwarded — fall through to the status-code wording.
            }

            return error switch
            {
                // "Client authentication failed", but NOT usually about the client id/secret. osu-web
                // is Laravel Passport over league/oauth2-server, whose validateRedirectUri throws
                // invalidClient on a redirect MISMATCH (AbstractGrant::validateRedirectUri) — the same
                // error as genuinely bad credentials. Verified against a live client whose id and
                // secret work perfectly for other grants: a wrong redirect_uri returns exactly this,
                // while the right one gets through to "Cannot decrypt the authorization code". So the
                // redirect URI is named first, because it is by far the likelier cause and the one
                // the raw error hides.
                "invalid_client" =>
                    $"osu! rejected the sign-in. Almost always this means your OAuth application's Redirect URI is blank or different — set it to exactly {RedirectUri} on your osu! account page. If it is already set to that, re-check the client id and secret in Settings → Online.",
                "invalid_grant" => "That sign-in attempt expired or was already used. Try connecting again.",
                "invalid_request" or "redirect_uri_mismatch" =>
                    $"osu! rejected the redirect URL. Set your OAuth application's Redirect URI to exactly {RedirectUri} on your osu! account page, then try again.",
                _ => $"osu! sign-in failed ({statusCode}).",
            };
        }

        /// <summary>Who the token belongs to — the username shown in Settings once connected.</summary>
        public async Task<(int Id, string Username)> FetchSelfAsync(string accessToken, CancellationToken ct = default)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, meEndpoint);
            message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            message.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await http.SendAsync(message, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new OsuOAuthException($"Couldn't read your osu! profile ({(int)response.StatusCode}).");

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = document.RootElement;

            int id = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out int value) ? value : 0;
            string username = root.TryGetProperty("username", out var name) ? name.GetString() ?? string.Empty : string.Empty;

            return (id, username);
        }
    }

    /// <summary>A token pair and when the access half stops working.</summary>
    /// <param name="AccessToken">Bearer token for API calls.</param>
    /// <param name="RefreshToken">Used once to obtain the next pair; osu! rotates it on every refresh.</param>
    /// <param name="ExpiresAt">When <paramref name="AccessToken"/> expires, in UTC.</param>
    public readonly record struct OsuTokenSet(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

    /// <summary>
    /// A sign-in failure whose <see cref="Exception.Message"/> is written for the user and shown
    /// verbatim in Settings — so, like <see cref="OfficialSearchException"/>, it must never carry a
    /// token, a secret, or a raw response body.
    /// </summary>
    public class OsuOAuthException : Exception
    {
        public OsuOAuthException(string message)
            : base(message)
        {
        }
    }
}
