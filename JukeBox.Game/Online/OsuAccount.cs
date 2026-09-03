#nullable enable

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Configuration;
using osu.Framework.Bindables;
using osu.Framework.Logging;

namespace JukeBox.Game.Online
{
    /// <summary>
    /// The signed-in osu! account: the stored tokens, whether we are connected, and the one method
    /// everything else calls — <see cref="GetAccessTokenAsync"/>, which hands back a token that is
    /// valid right now, refreshing it first if it isn't.
    ///
    /// <para>
    /// Tokens live in the config file beside the client secret, and are treated the same way: never
    /// logged, never surfaced in an error message, never shown in the UI. What the UI shows is the
    /// USERNAME, which is the only part a person needs to see to know whose account is connected.
    /// </para>
    /// </summary>
    public class OsuAccount
    {
        /// <summary>
        /// Refresh this long before the token actually expires. A token that dies mid-request costs
        /// a retry and a confusing error; a minute of slack costs nothing.
        /// </summary>
        private static readonly TimeSpan expiry_headroom = TimeSpan.FromMinutes(1);

        private readonly OsuOAuth oauth;
        private readonly JukeBoxConfigManager config;

        // Config-bindable COPIES held in fields: ConfigManager references what it hands back only
        // weakly, so copies nobody keeps alive are collected and the settings silently stop moving.
        private readonly Bindable<string> clientId;
        private readonly Bindable<string> clientSecret;
        private readonly Bindable<string> accessToken;
        private readonly Bindable<string> refreshToken;
        private readonly Bindable<string> expiresAt;

        /// <summary>The connected account's username, or empty when not connected. Bound by the UI.</summary>
        public readonly Bindable<string> Username = new Bindable<string>(string.Empty);

        /// <summary>The connected account's id — what the spectate feature resolves "me" against.</summary>
        public readonly Bindable<int> UserId = new Bindable<int>();

        /// <summary>Whether a refresh token is stored, i.e. whether we can act as the user at all.</summary>
        public readonly BindableBool IsConnected = new BindableBool();

        /// <summary>Serialises refreshes so a burst of callers mints one new token, not one each.</summary>
        private readonly SemaphoreSlim refreshLock = new SemaphoreSlim(1, 1);

        public OsuAccount(OsuOAuth oauth, JukeBoxConfigManager config)
        {
            this.oauth = oauth;
            this.config = config;

            clientId = config.GetBindable<string>(JukeBoxSetting.OsuClientId);
            clientSecret = config.GetBindable<string>(JukeBoxSetting.OsuClientSecret);
            accessToken = config.GetBindable<string>(JukeBoxSetting.OsuUserAccessToken);
            refreshToken = config.GetBindable<string>(JukeBoxSetting.OsuUserRefreshToken);
            expiresAt = config.GetBindable<string>(JukeBoxSetting.OsuUserTokenExpiry);

            Username.Value = config.Get<string>(JukeBoxSetting.OsuUserName);
            UserId.Value = config.Get<int>(JukeBoxSetting.OsuUserId);
            IsConnected.Value = refreshToken.Value.Length > 0;
        }

        /// <summary>Whether the client id/secret needed to sign in at all are present.</summary>
        public bool HasClientCredentials => clientId.Value.Trim().Length > 0 && clientSecret.Value.Trim().Length > 0;

        /// <summary>
        /// Runs the whole interactive sign-in: hold the loopback port, send the user to osu!'s own
        /// login page, catch the code, exchange it, and record who signed in.
        /// </summary>
        /// <param name="openBrowser">How to put the URL in front of the user. Injected rather than
        /// called directly so a test can drive the flow without launching a browser.</param>
        /// <param name="ct">Cancels the wait; the user closing the browser tab is the usual reason.</param>
        public async Task ConnectAsync(Action<string> openBrowser, CancellationToken ct = default)
        {
            if (!HasClientCredentials)
                throw new OsuOAuthException("Set your osu! OAuth client id and secret first (Settings → Online).");

            string state = OsuOAuth.NewState();

            using var listener = new LoopbackCallbackListener(state);

            // Started BEFORE the browser opens: osu! can redirect back faster than a listener can
            // be spun up, and a callback that arrives at a closed port is a dead sign-in.
            listener.Start();

            openBrowser(oauth.BuildAuthorizeUrl(clientId.Value.Trim(), state));

            string code = await listener.WaitForCodeAsync(ct).ConfigureAwait(false);

            var tokens = await oauth.ExchangeCodeAsync(clientId.Value.Trim(), clientSecret.Value.Trim(), code, ct: ct).ConfigureAwait(false);

            store(tokens);

            var (id, username) = await oauth.FetchSelfAsync(tokens.AccessToken, ct).ConfigureAwait(false);

            UserId.Value = id;
            Username.Value = username;
            config.SetValue(JukeBoxSetting.OsuUserId, id);
            config.SetValue(JukeBoxSetting.OsuUserName, username);
        }

        /// <summary>
        /// Forgets the account. Clears the tokens rather than merely flagging a disconnect: a
        /// refresh token left in the config file is a live credential, and "Disconnect" has to mean
        /// it is gone.
        /// </summary>
        public void Disconnect()
        {
            accessToken.Value = string.Empty;
            refreshToken.Value = string.Empty;
            expiresAt.Value = string.Empty;

            Username.Value = string.Empty;
            UserId.Value = 0;
            config.SetValue(JukeBoxSetting.OsuUserName, string.Empty);
            config.SetValue(JukeBoxSetting.OsuUserId, 0);

            IsConnected.Value = false;
        }

        /// <summary>
        /// A token that is valid right now, refreshing first if the stored one is spent. Null when
        /// no account is connected, which callers treat as "this feature is unavailable" rather
        /// than as an error.
        /// </summary>
        public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
        {
            if (!IsConnected.Value || refreshToken.Value.Length == 0)
                return null;

            await refreshLock.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                if (accessToken.Value.Length > 0 && DateTimeOffset.UtcNow < storedExpiry() - expiry_headroom)
                    return accessToken.Value;

                var tokens = await oauth.RefreshAsync(clientId.Value.Trim(), clientSecret.Value.Trim(), refreshToken.Value, ct)
                                        .ConfigureAwait(false);

                store(tokens);
                return tokens.AccessToken;
            }
            catch (OsuOAuthException e)
            {
                // A refresh token osu! no longer accepts (revoked in account settings, or expired
                // after long disuse) will never work again, so the connection is dropped rather
                // than retried forever. The MESSAGE is logged without the token itself.
                Logger.Log($"osu! account refresh failed ({e.Message}) — disconnecting.", level: LogLevel.Important);
                Disconnect();
                return null;
            }
            finally
            {
                refreshLock.Release();
            }
        }

        private DateTimeOffset storedExpiry()
            => DateTimeOffset.TryParse(expiresAt.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

        private void store(OsuTokenSet tokens)
        {
            accessToken.Value = tokens.AccessToken;

            // osu! ROTATES the refresh token on every refresh, so the new one replaces the old.
            // Keeping the original would work exactly once and then lock the account out.
            if (tokens.RefreshToken.Length > 0)
                refreshToken.Value = tokens.RefreshToken;

            expiresAt.Value = tokens.ExpiresAt.ToString("O", CultureInfo.InvariantCulture);
            IsConnected.Value = refreshToken.Value.Length > 0;
        }
    }
}
