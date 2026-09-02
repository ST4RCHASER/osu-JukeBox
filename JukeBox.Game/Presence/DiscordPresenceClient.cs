#nullable enable

using System;
using System.Text;
using DiscordRPC;
using DiscordRPC.Message;
using osu.Framework.Logging;

namespace JukeBox.Game.Presence;

/// <summary>
/// The real Discord IPC client: Lachee's discord-rpc-csharp (the same <c>DiscordRichPresence</c>
/// package, pinned to the same version, that osu!lazer's own <c>osu.Desktop/DiscordRichPresence.cs</c>
/// uses).
///
/// <para>
/// Every entry point here is total: Discord not running, Discord quitting mid-song, a sandbox that
/// forbids the socket — all of it has to end in a log line and nothing else, because rich presence
/// is decoration and must never be able to disturb playback. The library helps: with no Discord to
/// talk to, <see cref="DiscordRpcClient.Initialize"/> simply leaves the connection manager retrying
/// in the background, and presence set before that succeeds is picked up on
/// <see cref="DiscordRpcClient.OnReady"/> (see <see cref="onReady"/>).
/// </para>
/// </summary>
internal sealed class DiscordPresenceClient : IPresenceClient
{
    /// <summary>
    /// The Discord application whose name appears after the activity verb — "Listening to
    /// <em>this app's name</em>". It is the ONE value that has to be filled in for presence to
    /// appear at all, and it is deliberately left as a placeholder rather than borrowed: shipping
    /// somebody else's id (osu!lazer's, say) would advertise every listener as playing THEIR app.
    ///
    /// <para>
    /// To turn presence on for a build: create a free application at
    /// https://discord.com/developers/applications ("New Application", named exactly what the
    /// activity should read as — "osu!JukeBox"), copy its Application ID off the General
    /// Information page, and paste it here. Nothing else about the application needs configuring;
    /// no OAuth, no bot, no redirect URL. Until then <see cref="Start"/> short-circuits and the
    /// whole feature is inert — the setting still toggles, it just has no Discord to talk to.
    /// </para>
    /// </summary>
    public const string CLIENT_ID = "000000000000000000";

    private readonly string clientId;

    private DiscordRpcClient? client;

    /// <param name="clientId">Overridable only so a test can exercise the real connect path with a
    /// well-formed id; production always takes the default.</param>
    public DiscordPresenceClient(string clientId = CLIENT_ID)
    {
        this.clientId = clientId;
    }

    /// <summary>
    /// The last presence handed to <see cref="Publish"/>, kept so a connection that arrives (or
    /// comes back) later can be brought up to date — see <see cref="onReady"/>, which runs on the
    /// library's own thread. Null means "nothing should be showing", which is also what
    /// <see cref="Clear"/> leaves behind.
    /// </summary>
    private volatile PresenceState? current;

    /// <summary>Whether an application id has been filled in — the placeholder parses to zero.</summary>
    internal static bool IsUsableClientId(string value) => ulong.TryParse(value, out ulong id) && id != 0;

    public void Start()
    {
        if (client != null)
            return;

        if (!IsUsableClientId(clientId))
        {
            Logger.Log("Discord rich presence is built without an application id — see DiscordPresenceClient.CLIENT_ID.",
                LoggingTarget.Runtime, LogLevel.Debug);
            return;
        }

        try
        {
            var created = new DiscordRpcClient(clientId)
            {
                // We re-send only when the presence actually differs (see
                // DiscordPresenceService.NeedsRepublish), but a reconnect replays whatever we hold,
                // so let the library drop the duplicate rather than spend an IPC round trip on it.
                SkipIdenticalPresence = true,
            };

            created.OnReady += onReady;
            created.Initialize();
            client = created;
        }
        catch (Exception e)
        {
            // Known to fail in sandboxed environments (macOS app bundles, flatpak) where the IPC
            // socket isn't reachable. Nothing to recover — presence simply stays off this session.
            Logger.Log($"Discord rich presence unavailable: {e.Message}", LoggingTarget.Runtime, LogLevel.Debug);
            client = null;
        }
    }

    public void Publish(PresenceState state)
    {
        current = state;

        if (client is not { IsInitialized: true })
            return;

        try
        {
            client.SetPresence(BuildRichPresence(state));
        }
        catch (Exception e)
        {
            Logger.Log($"Failed to publish Discord presence: {e.Message}", LoggingTarget.Runtime, LogLevel.Debug);
        }
    }

    public void Clear()
    {
        current = null;

        if (client is not { IsInitialized: true })
            return;

        try
        {
            client.ClearPresence();
        }
        catch (Exception e)
        {
            Logger.Log($"Failed to clear Discord presence: {e.Message}", LoggingTarget.Runtime, LogLevel.Debug);
        }
    }

    /// <summary>
    /// Fires on the library's own thread whenever the connection is established, including after
    /// Discord is restarted underneath us. Discord has forgotten our presence but the client hasn't,
    /// so <see cref="DiscordRpcClient.SkipIdenticalPresence"/> would swallow the re-send — clearing
    /// its cached copy first is what makes the presence come back (lazer's implementation does the
    /// same thing for the same reason).
    /// </summary>
    private void onReady(object sender, ReadyMessage args)
    {
        var rpc = client;

        if (rpc == null)
            return;

        try
        {
            if (rpc.CurrentPresence != null)
                rpc.SetPresence(null);

            if (current is { } state)
                rpc.SetPresence(BuildRichPresence(state));
        }
        catch (Exception e)
        {
            Logger.Log($"Failed to restore Discord presence after reconnect: {e.Message}", LoggingTarget.Runtime, LogLevel.Debug);
        }
    }

    /// <summary>
    /// The only place our model meets the wire format.
    /// <see cref="PresenceActivity"/> becomes Discord's activity verb: Listening renders as
    /// "Listening to &lt;app&gt;" (the Spotify shape), both watching variants as "Watching &lt;app&gt;".
    /// Timestamps are sent as a start/end PAIR or not at all — Discord draws its progress bar only
    /// when it has both, and a lone start would read as a stopwatch counting up forever.
    /// </summary>
    internal static RichPresence BuildRichPresence(PresenceState state) => new RichPresence
    {
        Type = state.Activity == PresenceActivity.Listening ? ActivityType.Listening : ActivityType.Watching,
        Details = ClampLength(state.Details),
        State = ClampLength(state.State),
        Timestamps = state.StartUtc is { } start && state.EndUtc is { } end
            ? new Timestamps(start.ToUniversalTime(), end.ToUniversalTime())
            : null,
    };

    private const int max_bytes = 128;

    /// <summary>U+200B. Named because it is invisible in source.</summary>
    private const char zero_width_space = '​';

    private static readonly int ellipsis_bytes = Encoding.UTF8.GetByteCount("…");

    /// <summary>
    /// Discord accepts presence strings of 2 to 128 BYTES (not characters) and rejects the whole
    /// update otherwise, so a long CJK title has to be cut by encoded length. Strings too short to
    /// qualify are padded with zero-width spaces rather than dropped. Same constraint and same
    /// trick as lazer's own clamp — see <c>osu.Desktop/DiscordRichPresence.clampLength</c>.
    /// </summary>
    internal static string ClampLength(string value)
    {
        string trimmed = value.Trim();

        if (trimmed.Length < 2)
            return trimmed.PadRight(2, zero_width_space);

        if (Encoding.UTF8.GetByteCount(trimmed) <= max_bytes)
            return trimmed;

        int length = trimmed.Length;

        while (length > 0 && Encoding.UTF8.GetByteCount(trimmed.AsSpan(0, length)) + ellipsis_bytes > max_bytes)
            length--;

        return trimmed[..length] + '…';
    }

    public void Dispose()
    {
        try
        {
            client?.Dispose();
        }
        catch (Exception e)
        {
            Logger.Log($"Failed to shut down the Discord client: {e.Message}", LoggingTarget.Runtime, LogLevel.Debug);
        }
        finally
        {
            client = null;
        }
    }
}
