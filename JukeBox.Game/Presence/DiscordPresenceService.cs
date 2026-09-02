#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Threading;

namespace JukeBox.Game.Presence;

/// <summary>
/// Publishes what the app is currently playing to Discord as a rich presence.
///
/// <para>
/// MAIN PROCESS ONLY. This is added by <see cref="JukeBoxGame"/>, never by
/// <see cref="JukeBoxGameBase"/> — exactly like <see cref="Detach.DetachedViewerManager"/> and for
/// the same reason: the detached viewer window and every test scene run on the BASE game, and a
/// second process (or a headless test) opening its own Discord connection would fight the real one
/// over the same activity slot. See <c>TestSceneDiscordPresence.PresenceIsNotWiredIntoTheBaseGame</c>.
/// </para>
///
/// <para>
/// The activity type follows what is actually on screen rather than what the audio is doing, since
/// that is what a viewer of the presence would see over the user's shoulder — see
/// <see cref="Build"/> for the precedence.
/// </para>
///
/// <para>
/// Updates are recomputed every frame but only SENT when the picture materially changed
/// (<see cref="NeedsRepublish"/>) and then only after <see cref="DEBOUNCE_MS"/> of quiet — the same
/// trailing-debounce shape lazer uses, which is what keeps a dragged seek bar from turning into one
/// IPC round trip per frame. Discord rate-limits presence updates, and a scrub is the one gesture
/// that can produce hundreds of them.
/// </para>
/// </summary>
public partial class DiscordPresenceService : Component
{
    /// <summary>
    /// Quiet period before a changed presence is actually sent. Matches lazer's own 200ms: long
    /// enough that a drag of any length collapses to a single update when the user lets go, short
    /// enough that a song change feels immediate.
    /// </summary>
    internal const double DEBOUNCE_MS = 200;

    /// <summary>
    /// How far the start/end pair may drift before it counts as a different picture. Sub-second
    /// jitter is inherent — the timestamps are derived from a clock that is itself advancing — and
    /// republishing on it would mean a permanent update every frame. Only a seek, a pause, or a
    /// rate change moves them further than this.
    /// </summary>
    internal const double TIMESTAMP_TOLERANCE_MS = 2000;

    [Resolved]
    private Jukebox jukebox { get; set; } = null!;

    [Resolved]
    private PlaybackController playback { get; set; } = null!;

    [Resolved]
    private JukeBoxConfigManager config { get; set; } = null!;

    [Resolved]
    private GameHost host { get; set; } = null!;

    private readonly IPresenceClient client;

    private readonly Bindable<bool> enabled = new Bindable<bool>(true);
    private readonly Bindable<bool> renderChart = new Bindable<bool>();

    private PresenceState? published;

    /// <summary>
    /// The presence the pending send was started for, or null when nothing is queued. What
    /// <see cref="UpdatePresence"/> compares against to decide whether the debounce window should be
    /// restarted or left to finish.
    /// </summary>
    private PresenceState? scheduled;

    private ScheduledDelegate? pendingUpdate;

    /// <summary>
    /// Whether the CURRENT set draws a storyboard, decided the same way the on-screen layer decides
    /// it (see <see cref="scanStoryboard"/>) so the presence can never claim something the player
    /// isn't showing.
    /// </summary>
    private bool hasStoryboard;

    /// <summary>
    /// Bumped on every set change so a storyboard scan that finishes after the song already moved on
    /// is discarded rather than applied to the wrong track.
    /// </summary>
    private int storyboardScanToken;

    /// <param name="client">The IPC boundary. Defaults to the real Discord client; tests pass a fake.</param>
    public DiscordPresenceService(IPresenceClient? client = null)
    {
        this.client = client ?? new DiscordPresenceClient();
    }

    /// <summary>Test-only: the presence currently believed to be showing, or null for none.</summary>
    internal PresenceState? Published => published;

    /// <summary>
    /// The live "render the chart" setting, for subclasses that assemble their own
    /// <see cref="ReadInputs"/>.
    /// </summary>
    protected bool ChartIsRendering => renderChart.Value;

    /// <summary>
    /// Whether this process may open the Discord connection at all. A headless host has no window
    /// (the same test <see cref="UI.SettingsOverlay"/> uses for its window-bound rows), and a
    /// headless host means a test run.
    ///
    /// <para>
    /// Belt to <see cref="JukeBoxGame"/>'s braces. The wiring now lives in JukeBox.Desktop so no
    /// fixture can construct this at all, but the cost of that being wrong again is severe and
    /// non-obvious: under NUnit, osu!framework's <c>GameHost.unobservedExceptionHandler</c> aborts
    /// the whole host on ANY unobserved task exception, and a cancelled pipe read is exactly that.
    /// One test-host connection therefore doesn't fail a presence test — it kills whatever fixture
    /// happens to be running. So the connection is gated on the thing that actually distinguishes
    /// the two worlds, not on where the wiring happens to sit.
    /// </para>
    ///
    /// <para>
    /// Only the CONNECTION is gated. Everything above the IPC boundary still runs, so the tests
    /// that drive this service against a fake client exercise the real update policy.
    /// </para>
    /// </summary>
    internal bool CanConnect => host.Window != null;

    [BackgroundDependencyLoader]
    private void load()
    {
        config.BindWith(JukeBoxSetting.DiscordRichPresence, enabled);
        config.BindWith(JukeBoxSetting.RenderChart, renderChart);
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        enabled.BindValueChanged(e =>
        {
            if (e.NewValue)
            {
                if (CanConnect)
                    client.Start();

                return;
            }

            // Turning it off has to take the presence down NOW, not after the debounce: the user
            // watching the setting is the one person guaranteed to be looking at the result.
            cancelPending();
            published = null;
            client.Clear();
        }, true);

        // The difficulty on screen is part of the presence, and switching it can also change whether
        // there is a storyboard at all (an .osu carries its own [Events]) — so both of these rescan.
        playback.Current.BindValueChanged(_ => rescanStoryboard(), true);
        playback.SelectedOsuFile.BindValueChanged(_ => rescanStoryboard());
    }

    protected override void Update()
    {
        base.Update();
        UpdatePresence();
    }

    /// <summary>
    /// One pass of "is what Discord shows still right?". Called once per frame; <c>internal</c> so a
    /// test can drive several passes within one frame, which is what a seek-bar drag looks like from
    /// here and the only way to exercise the debounce without depending on the test host's frame rate.
    /// </summary>
    internal void UpdatePresence()
    {
        if (!enabled.Value)
            return;

        var next = ReadInputs() is { } inputs ? Build(inputs) : null;

        if (next == null)
        {
            // Nothing playing. Drop the presence rather than leaving the last song frozen on it.
            if (published == null)
                return;

            cancelPending();
            published = null;
            client.Clear();
            return;
        }

        if (!NeedsRepublish(published, next))
            return;

        // Trailing debounce, restarted by CHANGE rather than by difference-from-published: a drag
        // moves the presence every frame and must restart the window every frame, but once the user
        // lets go the same presence recurs frame after frame and the window has to be left alone to
        // finish. (Restarting on difference-from-published instead would mean a seek followed by
        // stillness — a seek then a pause, say — never landed at all.)
        if (scheduled != null && !NeedsRepublish(scheduled, next))
            return;

        pendingUpdate?.Cancel();
        scheduled = next;
        pendingUpdate = Scheduler.AddDelayed(() =>
        {
            scheduled = null;
            pendingUpdate = null;

            // Recomputed at send time rather than captured: after a debounce window the position has
            // moved on, and publishing the stale timestamps would put the progress bar behind.
            var current = ReadInputs() is { } latest ? Build(latest) : null;

            if (current == null)
                return;

            published = current;
            client.Publish(current);
        }, DEBOUNCE_MS);
    }

    private void cancelPending()
    {
        pendingUpdate?.Cancel();
        pendingUpdate = null;
        scheduled = null;
    }

    /// <summary>
    /// Everything the presence is derived from, gathered off the live playback state.
    /// <c>internal virtual</c> so tests can drive the debounce and publishing behaviour with
    /// synthetic input instead of a real audio track.
    /// </summary>
    internal virtual PresenceInputs? ReadInputs()
    {
        var set = jukebox.NowPlaying.Value;

        if (set == null)
            return null;

        return new PresenceInputs(
            Title: set.DisplayTitle,
            Artist: set.DisplayArtist,
            Difficulty: currentDifficultyName(),
            HasStoryboard: hasStoryboard,
            RenderChart: renderChart.Value,
            IsPlaying: playback.IsPlaying,
            PositionMs: playback.CurrentTimeMs,
            LengthMs: playback.LengthMs,
            // The clock's own rate already folds in the speed setting, replay rate mods and chart
            // rate mods, so the timestamps scale correctly without this having to know about any
            // of them individually.
            Rate: playback.PlaybackClock.Rate,
            NowUtc: DateTime.UtcNow,
            OnlineSetId: set.Id);
    }

    private string? currentDifficultyName()
    {
        string? osuFile = playback.SelectedOsuFile.Value;
        var set = playback.Current.Value;

        if (osuFile == null || set == null)
            return null;

        string? version = set.Difficulties.FirstOrDefault(d => d.Path == osuFile)?.Version;

        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    /// <summary>
    /// What Discord should be showing, given the state of the app. The precedence is the whole point
    /// of this method:
    /// <list type="number">
    /// <item>the rendered chart is on screen → <see cref="PresenceActivity.WatchingChart"/>;</item>
    /// <item>otherwise the map draws a storyboard → <see cref="PresenceActivity.WatchingStoryboard"/>;</item>
    /// <item>otherwise it is just music → <see cref="PresenceActivity.Listening"/>.</item>
    /// </list>
    /// Chart wins over storyboard because the chart is drawn ON TOP of the storyboard: when both are
    /// present the chart is what a viewer would actually be looking at.
    /// </summary>
    /// <returns>The presence to show, or null when there is nothing worth showing.</returns>
    internal static PresenceState? Build(PresenceInputs inputs)
    {
        string title = inputs.Title.Trim();
        string artist = inputs.Artist.Trim();

        if (title.Length == 0 && artist.Length == 0)
            return null;

        var activity = inputs.RenderChart ? PresenceActivity.WatchingChart
            : inputs.HasStoryboard ? PresenceActivity.WatchingStoryboard
            : PresenceActivity.Listening;

        string prefix = activity switch
        {
            PresenceActivity.WatchingChart => "chart · ",
            PresenceActivity.WatchingStoryboard => "storyboard · ",
            _ => string.Empty,
        };

        string state = artist;

        if (inputs.Difficulty is { } difficulty && difficulty.Trim().Length > 0)
            state = state.Length > 0 ? $"{state} · [{difficulty.Trim()}]" : $"[{difficulty.Trim()}]";

        string? imageUrl = CoverUrl(inputs.OnlineSetId);
        string? imageText = imageUrl == null ? null
            : artist.Length > 0 && title.Length > 0 ? $"{artist} - {title}"
            : title.Length > 0 ? title
            : artist;

        DateTime? start = null;
        DateTime? end = null;

        // No progress bar while paused (Discord's would keep counting), nor for a track of unknown
        // length, nor at a nonsensical rate — a stopped clock reports rate 0 in some states and
        // dividing by it would put the whole track into one instant.
        if (inputs.IsPlaying && inputs.LengthMs > 0 && inputs.Rate > 0)
        {
            double position = Math.Clamp(inputs.PositionMs, 0, inputs.LengthMs);

            start = inputs.NowUtc - TimeSpan.FromMilliseconds(position / inputs.Rate);
            end = inputs.NowUtc + TimeSpan.FromMilliseconds((inputs.LengthMs - position) / inputs.Rate);
        }

        return new PresenceState(activity, prefix + title, state, start, end, imageUrl, imageText);
    }

    /// <summary>
    /// osu!'s published cover art for a beatmap set — the map's own background, which is what the
    /// user is looking at while it plays, and the same image
    /// <see cref="UI.NowPlayingPanel"/> already shows as the now-playing artwork.
    ///
    /// <para>
    /// This particular variant on purpose: of everything osu! publishes for a set it is the only
    /// near-square one (160x120), and Discord renders the large image in a square. The
    /// <c>covers/</c> family is all letterbox strips by comparison — <c>card</c> is 400x100 and
    /// <c>cover</c> 900x250, which would show as a thin band in that slot.
    /// </para>
    /// </summary>
    /// <param name="onlineSetId">The set's osu! id, or 0 for a local or dropped map.</param>
    /// <returns>The cover URL, or null when there is no online set to have one.</returns>
    internal static string? CoverUrl(int onlineSetId)
    {
        if (onlineSetId <= 0)
            return null;

        // No length guard: Discord rejects an image reference over
        // DiscordPresenceClient.MAX_IMAGE_REFERENCE_LENGTH characters and the library's setter
        // throws rather than truncating, but a ten-digit id can only ever make this about forty
        // characters. The cap is held by construction, and asserted as a property in the tests
        // rather than re-checked here on every song change.
        return $"https://b.ppy.sh/thumb/{onlineSetId}l.jpg";
    }

    /// <summary>
    /// Whether <paramref name="next"/> is a different picture from what is already showing. Text
    /// differences always count; timestamps only count past <see cref="TIMESTAMP_TOLERANCE_MS"/>,
    /// which is what separates a seek or a rate change (a jump) from the frame-to-frame rounding of
    /// a track playing normally (a few milliseconds, forever).
    /// </summary>
    internal static bool NeedsRepublish(PresenceState? published, PresenceState next)
    {
        if (published == null)
            return true;

        if (published.Activity != next.Activity || published.Details != next.Details || published.State != next.State)
            return true;

        // Two different sets can carry the same title and artist (a remap, a different mapper's
        // upload), and their covers are still different pictures.
        if (published.ImageUrl != next.ImageUrl)
            return true;

        return !withinTolerance(published.StartUtc, next.StartUtc) || !withinTolerance(published.EndUtc, next.EndUtc);
    }

    private static bool withinTolerance(DateTime? a, DateTime? b)
    {
        // Gaining or losing the pair outright (pause, resume, a track of unknown length) is always a
        // change — that is the difference between showing a progress bar and showing none.
        if (a == null || b == null)
            return a == null && b == null;

        return Math.Abs((a.Value - b.Value).TotalMilliseconds) <= TIMESTAMP_TOLERANCE_MS;
    }

    private void rescanStoryboard()
    {
        int token = Interlocked.Increment(ref storyboardScanToken);
        var set = playback.Current.Value;
        string? osuFile = playback.SelectedOsuFile.Value ?? set?.PreferredOsuFile;

        hasStoryboard = false;

        if (set == null || (osuFile == null && set.OsbFile == null))
            return;

        // Off the update thread: this parses the .osu/.osb from disk, which is cheap once per song
        // but not something to do inside a frame.
        var scanning = set;
        string? osb = set.OsbFile;

        // Off the update thread, and TOTAL: nothing in this body may throw, because a faulted task
        // nobody observes is not a quiet failure here. Under NUnit, osu!framework's
        // GameHost.unobservedExceptionHandler aborts the entire host on any unobserved task
        // exception, so a stray fault on this path would kill whichever unrelated fixture happened
        // to be running — the same shape of failure the Discord pipe caused. The previous
        // ContinueWith form also left an antecedent fault unobserved whenever the scan did not
        // complete successfully; folding the continuation into the body removes both hazards.
        Task.Run(() =>
        {
            try
            {
                bool found = scanStoryboard(osuFile, osb);

                Schedule(() =>
                {
                    // The song (or difficulty) moved on while we were reading — this answer is
                    // about a track nobody is listening to any more.
                    if (token != Volatile.Read(ref storyboardScanToken) || !ReferenceEquals(playback.Current.Value, scanning))
                        return;

                    hasStoryboard = found;
                });
            }
            catch (Exception e)
            {
                Logger.Log($"Storyboard scan for the presence line failed: {e.Message}", LoggingTarget.Runtime, LogLevel.Debug);
            }
        });
    }

    /// <summary>
    /// Whether this difficulty draws anything. Decoded through the very same call the on-screen
    /// layer uses (<see cref="LazerStoryboardLayer.DecodeStoryboard"/>) and answered with the same
    /// "is there anything to show" test that layer applies, so the presence and the player can never
    /// disagree about whether a storyboard exists.
    /// </summary>
    private static bool scanStoryboard(string? osuFile, string? osbFile)
    {
        try
        {
            var storyboard = LazerStoryboardLayer.DecodeStoryboard(osuFile, osbFile);

            return storyboard.HasDrawable || storyboard.PrimaryVideo != null;
        }
        catch (Exception e)
        {
            // The radio downloads arbitrary community content; a malformed .osb must cost nothing
            // more than a slightly wrong presence line.
            Logger.Log($"Could not determine storyboard presence for the current track: {e.Message}",
                LoggingTarget.Runtime, LogLevel.Debug);
            return false;
        }
    }

    protected override void Dispose(bool isDisposing)
    {
        pendingUpdate?.Cancel();
        client.Dispose();
        base.Dispose(isDisposing);
    }
}

/// <summary>
/// The live state <see cref="DiscordPresenceService.Build"/> reads, as a value so the decision is a
/// pure function of it.
/// </summary>
/// <param name="Title">Romanized track title, as shown in the app.</param>
/// <param name="Artist">Romanized artist, as shown in the app.</param>
/// <param name="Difficulty">Name of the difficulty on screen, or null when there isn't one.</param>
/// <param name="HasStoryboard">Whether the difficulty on screen actually draws a storyboard.</param>
/// <param name="RenderChart">Whether the chart renderer is switched on.</param>
/// <param name="IsPlaying">False while paused or stopped.</param>
/// <param name="PositionMs">Current position within the track.</param>
/// <param name="LengthMs">Track length; 0 when unknown.</param>
/// <param name="Rate">The playback clock's effective rate; 1 is normal speed.</param>
/// <param name="NowUtc">The instant the timestamps are anchored to.</param>
/// <param name="OnlineSetId">The set's osu! id, or 0 for a local or dropped map with no online
/// listing (and therefore no published cover art).</param>
public readonly record struct PresenceInputs(
    string Title,
    string Artist,
    string? Difficulty,
    bool HasStoryboard,
    bool RenderChart,
    bool IsPlaying,
    double PositionMs,
    double LengthMs,
    double Rate,
    DateTime NowUtc,
    int OnlineSetId = 0);
