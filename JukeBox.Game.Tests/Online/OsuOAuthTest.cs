#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Configuration;
using JukeBox.Game.Online;
using NUnit.Framework;
using osu.Framework.Platform;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Online
{
    /// <summary>
    /// The osu! sign-in: the authorization-code flow against the user's OWN registered app.
    ///
    /// <para>
    /// Every network call is mocked. What these cover is the part that is easy to get subtly wrong
    /// and impossible to notice — that the authorize URL asks for a CODE rather than a token, that
    /// the rotated refresh token replaces the stored one, that a CSRF state mismatch is refused,
    /// and that the redirect-URI failure produces the one sentence that tells the user what to fix.
    /// </para>
    /// </summary>
    [TestFixture]
    public class OsuOAuthTest
    {
        private static JukeBoxConfigManager newConfig() =>
            new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-oauth-test", Path.GetRandomFileName())));

        /// <summary>A token endpoint that answers whatever the test tells it to, and records what it was asked.</summary>
        private class FakeTokenHandler : HttpMessageHandler
        {
            public readonly List<Dictionary<string, string>> Requests = new List<Dictionary<string, string>>();

            public HttpStatusCode Status = HttpStatusCode.OK;
            public string Body = "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\",\"expires_in\":86400}";
            public string MeBody = "{\"id\":4242,\"username\":\"Tester\"}";

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.RequestUri!.AbsolutePath.Contains("/me"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(MeBody, Encoding.UTF8, "application/json"),
                    };
                }

                string form = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
                var parsed = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (string pair in form.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] kv = pair.Split('=', 2);
                    parsed[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
                }

                Requests.Add(parsed);

                return new HttpResponseMessage(Status)
                {
                    Content = new StringContent(Body, Encoding.UTF8, "application/json"),
                };
            }
        }

        // ---- The authorize URL -------------------------------------------------------------------

        /// <summary>
        /// `response_type=code` is the difference between the flow we are allowed to use and the
        /// implicit one; getting it wrong still "works" in a browser, which is why it is pinned.
        /// </summary>
        [Test]
        public void TheAuthorizeUrlAsksForACodeOnOsusOwnPage()
        {
            var oauth = new OsuOAuth(new HttpClient());

            string url = oauth.BuildAuthorizeUrl("12345", "STATE123");

            Assert.That(url, Does.StartWith(OsuOAuth.AUTHORIZE_ENDPOINT));
            Assert.That(url, Does.Contain("response_type=code"));
            Assert.That(url, Does.Contain("client_id=12345"));
            Assert.That(url, Does.Contain("state=STATE123"));
            Assert.That(url, Does.Contain("scope=public"));
            Assert.That(Uri.UnescapeDataString(url), Does.Contain(OsuOAuth.RedirectUri));

            // The two things that would make this the wrong flow entirely.
            Assert.That(url, Does.Not.Contain("response_type=token"));
            Assert.That(url, Does.Not.Contain("password"));
        }

        /// <summary>
        /// The redirect URI is a fixed loopback port on purpose — osu-web is Laravel Passport, which
        /// matches it as an exact string, so a per-run port could never match what the user
        /// registered. Pinned because "use an ephemeral port" is the natural instinct and it breaks
        /// sign-in for everyone.
        /// </summary>
        [Test]
        public void TheRedirectUriIsAFixedLoopbackUrlWeCanTellTheUserToRegister()
        {
            Assert.That(OsuOAuth.RedirectUri, Is.EqualTo($"http://localhost:{OsuOAuth.LOOPBACK_PORT}/callback"));
            Assert.That(OsuOAuth.RedirectUri, Does.StartWith("http://localhost:"));
        }

        [Test]
        public void EveryStateIsDifferentAndNotGuessable()
        {
            string a = OsuOAuth.NewState();
            string b = OsuOAuth.NewState();

            Assert.That(a, Is.Not.EqualTo(b));
            Assert.That(a.Length, Is.GreaterThanOrEqualTo(32));
        }

        // ---- Code exchange and refresh -----------------------------------------------------------

        [Test]
        public async Task ExchangingTheCodePostsAnAuthorizationCodeGrant()
        {
            var handler = new FakeTokenHandler();
            var oauth = new OsuOAuth(new HttpClient(handler));

            var tokens = await oauth.ExchangeCodeAsync("12345", "shhh", "the-code");

            var sent = handler.Requests[0];

            Assert.That(sent["grant_type"], Is.EqualTo("authorization_code"));
            Assert.That(sent["code"], Is.EqualTo("the-code"));
            Assert.That(sent["redirect_uri"], Is.EqualTo(OsuOAuth.RedirectUri));

            // The flow we are explicitly NOT allowed to use would post these instead.
            Assert.That(sent.ContainsKey("username"), Is.False);
            Assert.That(sent.ContainsKey("password"), Is.False);

            Assert.That(tokens.AccessToken, Is.EqualTo("access-1"));
            Assert.That(tokens.RefreshToken, Is.EqualTo("refresh-1"));
            Assert.That(tokens.ExpiresAt, Is.GreaterThan(DateTimeOffset.UtcNow));
        }

        [Test]
        public async Task RefreshingPostsARefreshGrant()
        {
            var handler = new FakeTokenHandler();
            var oauth = new OsuOAuth(new HttpClient(handler));

            await oauth.RefreshAsync("12345", "shhh", "old-refresh");

            var sent = handler.Requests[0];

            Assert.That(sent["grant_type"], Is.EqualTo("refresh_token"));
            Assert.That(sent["refresh_token"], Is.EqualTo("old-refresh"));
        }

        // ---- Error mapping -----------------------------------------------------------------------

        /// <summary>
        /// The failure this feature will actually hit. The user's OAuth app ships with a blank
        /// Redirect URI, so the first sign-in attempt fails until they set it — and osu!'s own
        /// `invalid_request` says nothing about which field is wrong.
        /// </summary>
        [Test]
        public void ARedirectUriMismatchTellsTheUserExactlyWhatToSet()
        {
            string message = OsuOAuth.DescribeTokenError("{\"error\":\"invalid_request\",\"hint\":\"redirect_uri\"}", 400);

            Assert.That(message, Does.Contain(OsuOAuth.RedirectUri));
            Assert.That(message, Does.Contain("Redirect URI"));
        }

        /// <summary>
        /// The bug a user actually hit. osu! answers a MISMATCHED REDIRECT URI with
        /// invalid_client / "Client authentication failed" — league/oauth2-server's
        /// validateRedirectUri throws invalidClient, the same error as genuinely bad credentials.
        /// Verified live against a client whose id and secret work fine for other grants: the wrong
        /// redirect returns exactly this, the right one gets through to "Cannot decrypt the
        /// authorization code". So the message must lead with the redirect URI, or it sends people
        /// to re-check credentials that were never the problem.
        /// </summary>
        [Test]
        public void InvalidClientLeadsWithTheRedirectUriNotTheCredentials()
        {
            string message = OsuOAuth.DescribeTokenError("{\"error\":\"invalid_client\"}", 401);

            Assert.That(message, Does.Contain(OsuOAuth.RedirectUri));
            Assert.That(message, Does.Contain("Redirect URI"));

            // Credentials are still mentioned, but only after — they are the less likely cause.
            Assert.That(message.IndexOf("Redirect URI", StringComparison.Ordinal),
                Is.LessThan(message.IndexOf("client id", StringComparison.Ordinal)),
                "the redirect URI must be named before the credentials");
        }

        /// <summary>
        /// A failed token exchange is the one response that could echo back the client secret we
        /// just posted, so the raw body must never reach a message the UI shows or the log keeps.
        /// </summary>
        [Test]
        public void AnErrorNeverEchoesTheResponseBody()
        {
            const string leaky = "{\"error\":\"nonsense\",\"secret\":\"SUPER-SECRET-VALUE\"}";

            string message = OsuOAuth.DescribeTokenError(leaky, 500);

            Assert.That(message, Does.Not.Contain("SUPER-SECRET-VALUE"));
            Assert.That(message, Does.Not.Contain("nonsense"));
            Assert.That(message, Does.Contain("500"));
        }

        [Test]
        public void AnUnparseableBodyStillProducesAUsableMessage()
        {
            string message = OsuOAuth.DescribeTokenError("<html>502 Bad Gateway</html>", 502);

            Assert.That(message, Does.Contain("502"));
            Assert.That(message, Does.Not.Contain("html"));
        }

        // ---- The loopback listener ---------------------------------------------------------------

        private static int freePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        [Test]
        public async Task TheListenerCapturesTheCodeThenStops()
        {
            int port = freePort();

            using var listener = new LoopbackCallbackListener("STATE123", port);
            listener.Start();

            var waiting = listener.WaitForCodeAsync();

            using (var client = new HttpClient())
                await client.GetAsync($"http://127.0.0.1:{port}/callback/?code=abc123&state=STATE123");

            Assert.That(await waiting, Is.EqualTo("abc123"));

            listener.Dispose();

            // Disposed means the port is genuinely released, not merely ignored — a listener that
            // outlived the sign-in would hold 7274 for the rest of the session and break the NEXT one.
            Assert.DoesNotThrow(() =>
            {
                var rebind = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
                rebind.Start();
                rebind.Stop();
            });
        }

        /// <summary>
        /// Without this check any page the user happens to visit could call our loopback URL with a
        /// code of its own and bind the app to someone else's account.
        /// </summary>
        [Test]
        public async Task ACallbackWithTheWrongStateIsRefused()
        {
            int port = freePort();

            using var listener = new LoopbackCallbackListener("EXPECTED", port);
            listener.Start();

            var waiting = listener.WaitForCodeAsync();

            using (var client = new HttpClient())
                await client.GetAsync($"http://127.0.0.1:{port}/callback/?code=abc123&state=ATTACKER");

            Assert.That(async () => await waiting, Throws.TypeOf<OsuOAuthException>());
        }

        [Test]
        public async Task DecliningConsentIsReportedAsSuch()
        {
            int port = freePort();

            using var listener = new LoopbackCallbackListener("STATE123", port);
            listener.Start();

            var waiting = listener.WaitForCodeAsync();

            using (var client = new HttpClient())
                await client.GetAsync($"http://127.0.0.1:{port}/callback/?error=access_denied&state=STATE123");

            Assert.That(async () => await waiting,
                Throws.TypeOf<OsuOAuthException>().With.Message.Contains("declined"));
        }

        /// <summary>
        /// When osu! refuses the request it renders the error in the BROWSER and never redirects, so
        /// the callback we are waiting on is never called. Before this bound the app waited forever
        /// on "waiting for osu!" while the real answer sat in a browser tab — which is exactly how
        /// the reported bug presented.
        /// </summary>
        [Test]
        public async Task ASignInThatOsuNeverCompletesGivesUpAndSaysWhy()
        {
            var handler = new FakeTokenHandler();
            var config = newConfig();
            var account = new OsuAccount(new OsuOAuth(new HttpClient(handler)), config);

            config.SetValue(JukeBoxSetting.OsuClientId, "12345");
            config.SetValue(JukeBoxSetting.OsuClientSecret, "shhh");

            // The browser gets the URL and nothing ever calls back — exactly what happens when osu!
            // refuses the request and renders the error on its own page instead of redirecting.
            var connecting = account.ConnectAsync(_ => { }, signInTimeout: TimeSpan.FromMilliseconds(250));

            // Bounded so a regression that removes the timeout FAILS here rather than hanging the suite.
            var finished = await Task.WhenAny(connecting, Task.Delay(TimeSpan.FromSeconds(10)));

            Assert.That(finished, Is.SameAs(connecting), "the sign-in never gave up — it would wait forever");

            var error = Assert.ThrowsAsync<OsuOAuthException>(async () => await connecting);

            // The diagnosis has to be in the message: the real error is in a browser tab we cannot read.
            Assert.That(error!.Message, Does.Contain(OsuOAuth.RedirectUri));
            Assert.That(account.IsConnected.Value, Is.False);
        }

        // ---- Token storage on the account --------------------------------------------------------

        [Test]
        public async Task ConnectingStoresTheTokensAndTheUsername()
        {
            var handler = new FakeTokenHandler();
            var config = newConfig();
            var account = new OsuAccount(new OsuOAuth(new HttpClient(handler)), config);

            config.SetValue(JukeBoxSetting.OsuClientId, "12345");
            config.SetValue(JukeBoxSetting.OsuClientSecret, "shhh");

            Assert.That(account.IsConnected.Value, Is.False);

            await connectThroughLoopback(account, handler);

            Assert.That(account.IsConnected.Value, Is.True);
            Assert.That(account.Username.Value, Is.EqualTo("Tester"));
            Assert.That(account.UserId.Value, Is.EqualTo(4242));
            Assert.That(config.Get<string>(JukeBoxSetting.OsuUserRefreshToken), Is.EqualTo("refresh-1"));
        }

        [Test]
        public async Task DisconnectingClearsTheStoredCredential()
        {
            var handler = new FakeTokenHandler();
            var config = newConfig();
            var account = new OsuAccount(new OsuOAuth(new HttpClient(handler)), config);

            config.SetValue(JukeBoxSetting.OsuClientId, "12345");
            config.SetValue(JukeBoxSetting.OsuClientSecret, "shhh");
            await connectThroughLoopback(account, handler);

            account.Disconnect();

            Assert.That(account.IsConnected.Value, Is.False);
            Assert.That(account.Username.Value, Is.Empty);

            // The refresh token is a live credential — "Disconnect" has to mean it is gone from the
            // config file, not merely that a flag flipped.
            Assert.That(config.Get<string>(JukeBoxSetting.OsuUserRefreshToken), Is.Empty);
            Assert.That(config.Get<string>(JukeBoxSetting.OsuUserAccessToken), Is.Empty);
            Assert.That(await account.GetAccessTokenAsync(), Is.Null);
        }

        /// <summary>
        /// osu! rotates the refresh token on every refresh. Storing the response's NEW one is what
        /// keeps the account connected; keeping the original would work exactly once.
        /// </summary>
        [Test]
        public async Task ARefreshRotatesTheStoredRefreshToken()
        {
            var handler = new FakeTokenHandler();
            var config = newConfig();
            var account = new OsuAccount(new OsuOAuth(new HttpClient(handler)), config);

            config.SetValue(JukeBoxSetting.OsuClientId, "12345");
            config.SetValue(JukeBoxSetting.OsuClientSecret, "shhh");
            await connectThroughLoopback(account, handler);

            // Expire what we hold, and have osu! answer with a different pair.
            config.SetValue(JukeBoxSetting.OsuUserTokenExpiry, DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"));
            handler.Body = "{\"access_token\":\"access-2\",\"refresh_token\":\"refresh-2\",\"expires_in\":86400}";

            string? token = await account.GetAccessTokenAsync();

            Assert.That(token, Is.EqualTo("access-2"));
            Assert.That(config.Get<string>(JukeBoxSetting.OsuUserRefreshToken), Is.EqualTo("refresh-2"),
                "the rotated refresh token must replace the old one, or the next refresh fails");
        }

        [Test]
        public async Task AValidTokenIsReusedWithoutRefreshing()
        {
            var handler = new FakeTokenHandler();
            var config = newConfig();
            var account = new OsuAccount(new OsuOAuth(new HttpClient(handler)), config);

            config.SetValue(JukeBoxSetting.OsuClientId, "12345");
            config.SetValue(JukeBoxSetting.OsuClientSecret, "shhh");
            await connectThroughLoopback(account, handler);

            int callsAfterConnect = handler.Requests.Count;

            Assert.That(await account.GetAccessTokenAsync(), Is.EqualTo("access-1"));
            Assert.That(handler.Requests.Count, Is.EqualTo(callsAfterConnect), "a live token must not be refreshed");
        }

        /// <summary>
        /// A refresh token osu! has revoked will never work again, so the account is dropped rather
        /// than retried on every call for the rest of the session.
        /// </summary>
        [Test]
        public async Task ARejectedRefreshDisconnectsInsteadOfRetryingForever()
        {
            var handler = new FakeTokenHandler();
            var config = newConfig();
            var account = new OsuAccount(new OsuOAuth(new HttpClient(handler)), config);

            config.SetValue(JukeBoxSetting.OsuClientId, "12345");
            config.SetValue(JukeBoxSetting.OsuClientSecret, "shhh");
            await connectThroughLoopback(account, handler);

            config.SetValue(JukeBoxSetting.OsuUserTokenExpiry, DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"));
            handler.Status = HttpStatusCode.Unauthorized;
            handler.Body = "{\"error\":\"invalid_grant\"}";

            Assert.That(await account.GetAccessTokenAsync(), Is.Null);
            Assert.That(account.IsConnected.Value, Is.False);
            Assert.That(config.Get<string>(JukeBoxSetting.OsuUserRefreshToken), Is.Empty);
        }

        [Test]
        public void ConnectingWithoutClientCredentialsSaysWhatIsMissing()
        {
            var account = new OsuAccount(new OsuOAuth(new HttpClient(new FakeTokenHandler())), newConfig());

            Assert.That(async () => await account.ConnectAsync(_ => { }),
                Throws.TypeOf<OsuOAuthException>().With.Message.Contains("client id"));
        }

        /// <summary>Drives the real interactive flow, standing in for the browser by calling the
        /// loopback URL the way osu! would.</summary>
        private static async Task connectThroughLoopback(OsuAccount account, FakeTokenHandler handler)
        {
            var connecting = account.ConnectAsync(url =>
            {
                // The state the app generated, echoed back exactly as osu! would.
                string state = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["state"]!;

                _ = Task.Run(async () =>
                {
                    using var client = new HttpClient();
                    await client.GetAsync($"http://127.0.0.1:{OsuOAuth.LOOPBACK_PORT}/callback/?code=the-code&state={state}");
                });
            });

            await connecting;
        }
    }
}
