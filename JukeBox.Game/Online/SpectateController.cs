#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.Replays;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Platform;

namespace JukeBox.Game.Online;

/// <summary>
/// The app-wide home of spectating: it owns the watch list, decides when the
/// <see cref="SpectateSession"/> polls, and publishes what came back for the UI and the player box
/// to bind to.
///
/// <para>
/// A <see cref="Component"/> rather than a plain object because the poll cadence has to be driven
/// by something, and the game's own update loop is the one clock every part of the app already
/// agrees on. The actual work is not on that thread — <see cref="SpectateSession.PollAsync"/> runs
/// as an ordinary task and only its RESULT is published back on the update thread.
/// </para>
/// </summary>
public partial class SpectateController : Component
{
    /// <summary>
    /// How often a round runs while spectating is on.
    ///
    /// <para>
    /// A round costs one batched presence request plus one per watched player, so a full list of
    /// <see cref="SpectateWatchList.MAX_WATCHED"/> at this interval is around thirty requests a
    /// minute — comfortably inside osu!'s general allowance, and paced so a long list stays polite
    /// rather than being technically permitted. Freshness gives up little for it: replays are
    /// post-hoc by nature, and the score feed itself already lags several seconds.
    /// </para>
    /// </summary>
    public static readonly TimeSpan POLL_INTERVAL = TimeSpan.FromSeconds(20);

    [Resolved]
    private JukeBoxConfigManager config { get; set; } = null!;

    [Resolved]
    private BeatmapCache cache { get; set; } = null!;

    [Resolved]
    private ReplayStore replays { get; set; } = null!;

    [Resolved]
    private GameHost host { get; set; } = null!;

    private readonly ISpectateApi api;

    private SpectateSession session = null!;

    private Bindable<string> storedWatchList = null!;

    /// <summary>Whether spectating is running. Session-only: a poll loop that resumed itself on
    /// launch would spend the download budget before anyone asked it to.</summary>
    public readonly BindableBool Active = new BindableBool();

    /// <summary>
    /// Bumped once per completed poll that changed something. Everything that draws spectate state
    /// binds to this rather than to the session, because the session's contents are snapshots — the
    /// revision is the only signal that a new one is worth taking.
    /// </summary>
    public readonly BindableInt Revision = new BindableInt();

    /// <summary>The watch list, as names. Setting it persists and re-seeds the session.</summary>
    public readonly BindableList<string> Watched = new BindableList<string>();

    private CancellationTokenSource? polling;
    private bool pollInFlight;
    private double nextPollTime;

    /// <summary>True while <see cref="replaceWatchList"/> is mid-swap — see its remarks.</summary>
    private bool editingWatchList;

    /// <param name="api">osu!, or a fake. Passed in rather than built here because the real one
    /// needs the app's single <see cref="System.Net.Http.HttpClient"/>, which is owned by
    /// <c>JukeBoxGameBase</c> and deliberately not in DI.</param>
    public SpectateController(ISpectateApi api)
    {
        this.api = api;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        string replayDirectory = host.Storage.GetFullPath("spectate", true);

        session = new SpectateSession(
            api,
            new ReplayDownloadBudget(),
            (setId, ct) => cache.GetAsync(setId, ct),
            replayDirectory);

        storedWatchList = config.GetBindable<string>(JukeBoxSetting.SpectateWatchedUsers);

        Watched.AddRange(SpectateWatchList.Parse(storedWatchList.Value));
        session.SetWatched(Watched);

        Watched.BindCollectionChanged((_, _) =>
        {
            // Suppressed mid-edit: an edit is a Clear followed by an AddRange, and reacting to the
            // Clear would persist an EMPTY watch list to the config file and hand the session an
            // empty roster — throwing away every resolved id and loaded play for the players who
            // were never removed. The list is one value, so it changes once.
            if (editingWatchList)
                return;

            publishWatchList();
        });

        Active.BindValueChanged(e =>
        {
            if (e.NewValue)
            {
                // Poll immediately on start rather than after a full interval: the first thing a
                // person does after pressing the button is look at it.
                nextPollTime = double.MinValue;
            }
            else
            {
                polling?.Cancel();
                Revision.Value++;
            }
        });
    }

    /// <summary>
    /// The bearer token spectating runs on: the signed-in user's when there is one, and the app-only
    /// client-credentials token otherwise.
    ///
    /// <para>
    /// Both carry the <c>public</c> scope every spectate endpoint needs — verified live, replay
    /// downloads included — so signing in is not a requirement of the feature: it changes WHOSE
    /// quota is being spent, not what is reachable. Preferring the user's token is the honest
    /// default for that reason.
    /// </para>
    /// </summary>
    public static async Task<string?> TokenAsync(OsuAccount account, OfficialBeatmapSearch officialSearch, CancellationToken ct)
    {
        string? user = await account.GetAccessTokenAsync(ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(user))
            return user;

        return officialSearch.HasCredentials ? await officialSearch.AcquireTokenAsync(ct).ConfigureAwait(false) : null;
    }

    /// <summary>Every watched player and what is known about them, freshly snapshotted.</summary>
    public IReadOnlyList<WatchedPlayer> Players => session.Players;

    /// <summary>The plays that should be on screen right now, already capped and ordered.</summary>
    public IReadOnlyList<SpectateEntry> Rendered => Active.Value ? session.Rendered : Array.Empty<SpectateEntry>();

    /// <summary>Adds a player to the watch list, reporting whether the list actually changed.</summary>
    public bool Add(string username)
    {
        var next = SpectateWatchList.Add(Watched, username);

        if (next.Count == Watched.Count)
            return false;

        replaceWatchList(next);
        return true;
    }

    /// <summary>Drops a player from the watch list.</summary>
    public void Remove(string username) => replaceWatchList(SpectateWatchList.Remove(Watched, username));

    /// <summary>
    /// Swaps the whole list in one go, then publishes once. The two-step Clear/AddRange is an
    /// implementation detail of <see cref="BindableList{T}"/> and must not be observable — see the
    /// handler in <see cref="load"/>.
    /// </summary>
    private void replaceWatchList(IReadOnlyList<string> names)
    {
        editingWatchList = true;

        try
        {
            Watched.Clear();
            Watched.AddRange(names);
        }
        finally
        {
            editingWatchList = false;
        }

        publishWatchList();
    }

    /// <summary>Persists the list, re-seeds the session with it, and tells the UI to redraw.</summary>
    private void publishWatchList()
    {
        storedWatchList.Value = SpectateWatchList.Format(Watched);
        session.SetWatched(Watched);
        Revision.Value++;
    }

    protected override void Update()
    {
        base.Update();

        if (!Active.Value || pollInFlight || Clock.CurrentTime < nextPollTime)
            return;

        pollInFlight = true;
        polling = new CancellationTokenSource();

        var token = polling.Token;

        Task.Run(async () =>
        {
            try
            {
                return await session.PollAsync(token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Logger.Log($"[spectate] poll failed: {e.Message}", level: LogLevel.Important);
                return false;
            }
        }, token).ContinueWith(t => Schedule(() =>
        {
            pollInFlight = false;
            nextPollTime = Clock.CurrentTime + POLL_INTERVAL.TotalMilliseconds;

            if (t.Status == TaskStatus.RanToCompletion && t.Result)
                publish();
        }), TaskScheduler.Default);
    }

    /// <summary>
    /// Publishes a finished round.
    ///
    /// <para>
    /// The registration into <see cref="ReplayStore"/> is what makes the SAME-map case work without
    /// a renderer of its own: a watched play lands under the .osu it was played on, so if the user
    /// happens to be listening to that difficulty, the existing multi-replay grid and combine views
    /// pick it up through <see cref="ReplayStore.AllForOsuFile"/> exactly as they do for a dropped
    /// replay file. The independent panes handle everything else.
    /// </para>
    /// </summary>
    private void publish()
    {
        foreach (var entry in session.Rendered)
            replays.Register(entry.Replay);

        Revision.Value++;
    }

    protected override void Dispose(bool isDisposing)
    {
        polling?.Cancel();
        polling?.Dispose();

        base.Dispose(isDisposing);
    }
}
