#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Online;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;

namespace JukeBox.Game.Playback;

/// <summary>
/// Conducts continuous playback: pulls from the queue (falling back to the radio when it's
/// empty), caches the picked set, hands it to the <see cref="PlaybackController"/>, and repeats
/// whenever a track finishes, is skipped, or playback is (re)started while idle.
/// </summary>
public partial class Jukebox : Component
{
    /// <summary>First retry delay after the radio fails to find anything.</summary>
    private const double radio_retry_base_ms = 5000;

    /// <summary>
    /// Ceiling on the retry delay. Doubling without one meant a radio that failed against dead
    /// mirrors hammered them every five seconds forever; a ceiling keeps recovery prompt (a mirror
    /// that comes back is picked up within a minute) without the tight loop.
    /// </summary>
    private const double radio_retry_max_ms = 60000;

    /// <summary>
    /// Consecutive failed radio lookups, driving the backoff below. Reset the moment a lookup
    /// succeeds, so one bad minute doesn't leave the radio sluggish afterwards.
    /// </summary>
    private int radioFailures;

    private double radioRetryDelay =>
        Math.Min(radio_retry_base_ms * Math.Pow(2, Math.Max(0, radioFailures - 1)), radio_retry_max_ms);

    /// <summary>
    /// The delay the next radio retry would be scheduled with — the very value handed to
    /// <c>AddDelayed</c>. Exposed so a test can assert the backoff grows and is capped without
    /// spending a real minute waiting for the scheduler to prove it.
    /// </summary>
    internal double RadioRetryDelayMs => radioRetryDelay;

    public readonly Bindable<string?> LastError = new();

    /// <summary>
    /// The <see cref="BeatmapSetInfo"/> metadata (title/artist/etc.) for whatever
    /// <see cref="PlaybackController"/> is currently playing — set alongside <see cref="PlaybackController.PlayAsync"/>
    /// in each advance round, so UI (e.g. a now-playing bar) has a title/artist to display without
    /// having to derive it from <see cref="Beatmaps.CachedBeatmapSet"/>, which carries none.
    /// </summary>
    public readonly Bindable<BeatmapSetInfo?> NowPlaying = new();

    /// <summary>
    /// Human-readable progress feedback for whatever the current advance round is doing that isn't
    /// instant, in the order a round reaches them: "Looking for a song…" while the radio searches a
    /// mirror, "Downloading {title}…" while a cache-miss download runs, "Preparing {title}…" while
    /// that download is extracted and its audio loaded. Null the rest of the time. UI (e.g.
    /// <see cref="UI.NowPlayingPanel"/>) shows this so a slow round doesn't look like the app hung.
    ///
    /// <para>
    /// A cache HIT deliberately writes nothing at all: that path is fast, and a status line that
    /// flashes on and straight back off for every skip through already-cached songs is worse than
    /// no line. Everything that keys off this — the panel's spinner, and
    /// <see cref="CanSkipNext"/> — inherits that, so the fast path stays visually still.
    /// </para>
    /// </summary>
    public readonly Bindable<string?> Status = new();

    /// <summary>
    /// Whether pressing next can currently change what plays next. False only while the radio is
    /// mid-lookup with an empty queue: no song has been identified yet, so pressing next can do
    /// nothing but abandon that search and start an identical one, making the wait longer. UI binds
    /// this to disable its skip control (see <see cref="UI.TransportRow"/>).
    ///
    /// <para>
    /// Deliberately NOT false for the whole of a slow round. While a DOWNLOAD is running there is a
    /// real song to skip past, and skipping past it is exactly what the per-caller cancellation in
    /// <see cref="BeatmapCache.GetAsync"/> exists to make instant — disabling next there would trap
    /// the user behind a stalled download with no way out, which is a worse failure than the silent
    /// wait this was added to fix. The queue term matters for the same reason: anything queued is
    /// something concrete to skip TO, so next stays live even mid-lookup.
    /// </para>
    /// </summary>
    public readonly Bindable<bool> CanSkipNext = new(true);

    /// <summary>
    /// The set id whose download the current advance round is waiting on, or null when it isn't
    /// waiting on one. Set and cleared alongside <see cref="Status"/>; separate from it because a
    /// percentage changes far too often to push through a bindable — UI pairs this id with
    /// <see cref="BeatmapCache.TryGetDownloadProgress"/>, which it can poll cheaply, to turn
    /// "Downloading {title}…" into "Downloading {title}… 42%".
    /// </summary>
    public readonly Bindable<int?> DownloadingSetId = new();

    /// <summary>
    /// Raised on the update thread when a set is newly added to the queue via
    /// <see cref="EnqueueAndMaybePlayAsync"/> — for the "Added to queue: X" toast. Not raised for a
    /// pick that was already queued (<see cref="MusicQueue.Enqueue"/> dedupes by set id), since
    /// nothing changed. Subscribers live as long as this component does, matching how
    /// <see cref="PlaybackController.TrackCompleted"/> is consumed below.
    /// </summary>
    public event Action<BeatmapSetInfo>? Enqueued;

    /// <summary>
    /// Cache size limit in bytes, checked after every successful <see cref="BeatmapCache.GetAsync"/>
    /// via <see cref="BeatmapCache.EvictToLimit"/>. Set from <c>JukeBoxSetting.CacheSizeGb</c> by
    /// the owner (see <see cref="JukeBoxGameBase"/>); this class has no config dependency itself.
    /// Defaults to 10 GB so eviction still behaves sensibly if never set.
    /// </summary>
    public long CacheLimitBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    private readonly MusicQueue queue;
    private readonly RadioService radio;
    private readonly BeatmapCache cache;
    private readonly PlaybackController playback;

    // Reentrancy guard with latest-wins coalescing, mirroring the arbitration approach
    // PlaybackController uses for overlapping PlayAsync calls: TrackCompleted, SkipCurrent and
    // Start can all race to call Advance while one is already in flight (most importantly, a
    // manual skip landing mid-download). Rather than dropping that request, `pendingAdvance`
    // records it; the in-flight round runs one more round immediately after finishing instead of
    // releasing the guard, so multiple overlapping requests coalesce into exactly one extra
    // round (not one per request) and none are lost. Both fields are only ever read/written
    // inside `advanceLock`, and the lock is never held across an `await`.
    private readonly object advanceLock = new();
    private bool advancing;
    private bool pendingAdvance;

    /// <summary>
    /// Cancelled when a newer advance request arrives, so the round in flight stops waiting on a
    /// download nobody wants any more and gets out of the way immediately.
    ///
    /// <para>
    /// Without this a skip was only recorded, never acted on: the round kept downloading the song
    /// being skipped, played it for a moment when it finally arrived, and only then ran the round
    /// the user actually asked for. Guarded by <see cref="advanceLock"/> along with the two flags
    /// above.
    /// </para>
    /// </summary>
    private CancellationTokenSource? roundCancellation;

    // Tracks whether whatever's currently playing (per NowPlaying) came from the queue or was
    // picked by the radio fallback — written in the same Schedule callback as NowPlaying so it's
    // updated on the update thread at the same point NowPlaying is, and read from
    // EnqueueAndMaybePlayAsync (also update-thread) to decide whether a newly-enqueued pick should
    // interrupt radio filler. Only ever meaningful once something has played at least once;
    // defaults to false (not radio) so an enqueue before anything has played doesn't spuriously
    // "interrupt" — the existing playback.Current.Value == null idle check already handles that case.
    private bool currentIsRadio;

    public Jukebox(MusicQueue queue, RadioService radio, BeatmapCache cache, PlaybackController playback)
    {
        this.queue = queue;
        this.radio = radio;
        this.cache = cache;
        this.playback = playback;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        playback.TrackCompleted += onTrackCompleted;

        // The queue is half of CanSkipNext's answer: enqueueing something during a lookup gives the
        // user a concrete song to skip to, so next has to come back to life without waiting for the
        // lookup to finish.
        queue.Items.CollectionChanged += (_, _) => updateCanSkipNext();
    }

    /// <summary>
    /// Whether the radio is mid-lookup. Update thread only, like <see cref="statusShownBy"/> — set
    /// from inside a <c>Schedule</c> either side of the search, and cleared again by the round's own
    /// exit path as a safety net so a round that throws mid-search can't leave next dead.
    /// </summary>
    private bool lookingUp;

    private void updateCanSkipNext() => CanSkipNext.Value = !lookingUp || queue.Items.Count > 0;

    /// <summary>
    /// The failure currently on the error line that keeps recurring, or null when the last round
    /// wasn't a repeat failure. Update thread only.
    ///
    /// <para>
    /// This is what collapses a repeating failure into ONE announcement: while it is set, the
    /// round-start clear is skipped, so re-reporting the same text leaves the bindable unchanged
    /// and nothing downstream fires again. A DIFFERENT failure still changes the value and is still
    /// announced, which is the behaviour worth keeping — the user needs to know when the problem
    /// changes.
    /// </para>
    /// </summary>
    private string? ongoingFailure;

    /// <summary>Reports a failure, collapsing consecutive identical ones — see <see cref="ongoingFailure"/>.</summary>
    private void announceFailure(string message) => Schedule(() =>
    {
        ongoingFailure = message;
        LastError.Value = message;
    });

    /// <summary>
    /// Marks the run of failures over. Deliberately does NOT clear <see cref="LastError"/> itself:
    /// a failure met on the way to an eventual success stays readable for the rest of the round,
    /// and the next round's start is what clears it.
    /// </summary>
    private void clearOngoingFailure() => Schedule(() => ongoingFailure = null);

    private void setLookingUp(bool value) => Schedule(() =>
    {
        lookingUp = value;
        updateCanSkipNext();
    });

    /// <summary>
    /// Marshals <paramref name="f"/> onto the update thread via <c>Schedule</c> and returns its
    /// result. <see cref="queue"/>'s <see cref="MusicQueue.Items"/> is a
    /// <see cref="BindableList{T}"/> that a UI consumer (e.g. <see cref="UI.QueuePanel"/>) binds
    /// <see cref="BindableList{T}.CollectionChanged"/> against to rebuild Drawables — mutating or
    /// even enumerating it off the update thread races that rebuild and can crash the framework
    /// (see the routed call sites below). advanceRoundAsync and evictCacheInBackground run with
    /// <c>ConfigureAwait(false)</c> continuations on the threadpool, so every touch of
    /// <see cref="queue"/> from either must go through this.
    /// </summary>
    private Task<T> onUpdateThread<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        Schedule(() =>
        {
            try
            {
                tcs.SetResult(f());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

    /// <summary>
    /// Whether an empty queue falls back to the radio. Off makes the queue authoritative: the last
    /// queued song ends and playback simply stops, with no lookup and therefore nothing to fail or
    /// report. Bound to <see cref="Configuration.JukeBoxSetting.RadioOnEmptyQueue"/> by the owner
    /// (see JukeBoxGameBase); defaults to on, which is what this class has always done.
    /// </summary>
    public readonly BindableBool RadioOnEmptyQueue = new BindableBool(true);

    /// <summary>
    /// Whether <see cref="Start"/> reaches for the radio when the queue is empty at launch.
    /// Defaults to on, matching what this class has always done: <see cref="Start"/> advanced
    /// unconditionally, and with nothing queued that advance was always a radio pick.
    ///
    /// <para>
    /// Deliberately NOT gated by <see cref="RadioOnEmptyQueue"/>: the two answer different
    /// questions ("greet me with music" versus "keep music coming once my queue runs dry"), and a
    /// user who wants one radio song at launch and silence afterwards can only say so if this fires
    /// on its own. So it grants the launch round one radio pick regardless — see
    /// <see cref="radioAllowedOnce"/>.
    /// </para>
    /// </summary>
    public readonly BindableBool RadioOnStart = new BindableBool(true);

    /// <summary>
    /// A one-shot permit for the radio, spent by the next round that finds the queue empty. Exists
    /// so <see cref="RadioOnStart"/> can fire a single pick without <see cref="RadioOnEmptyQueue"/>
    /// being on — and, being one-shot, without that launch pick turning into an endless station the
    /// user switched off. Update thread only, like the rest of the round's bookkeeping.
    /// </summary>
    private bool radioAllowedOnce;

    /// <summary>
    /// Kicks off playback if nothing has played yet (queue if non-empty, radio otherwise).
    /// A no-op once something is already playing.
    /// </summary>
    public void Start()
    {
        if (playback.Current.Value != null)
            return;

        // With something queued this is an ordinary advance and neither radio setting applies.
        // With nothing queued, only RadioOnStart can justify a lookup — and it justifies exactly
        // one, whatever RadioOnEmptyQueue says.
        if (queue.Items.Count == 0)
        {
            if (!RadioOnStart.Value)
                return;

            radioAllowedOnce = true;
        }

        AdvanceAsync();
    }

    /// <summary>
    /// Enqueues <paramref name="set"/>. If nothing has played yet, advances immediately so the
    /// newly-enqueued set (or whatever is now at the head of the queue) starts playing.
    /// </summary>
    /// <remarks>
    /// Must be called from the update thread — it touches <see cref="queue"/> directly rather
    /// than via <see cref="onUpdateThread{T}"/>, same as <see cref="Start"/> and
    /// <see cref="SkipCurrent"/>. Every current caller (UI event handlers such as
    /// MapIdOverlay/BeatmapListingOverlay's submit actions) already runs there.
    /// </remarks>
    /// <param name="set">The set to queue.</param>
    /// <param name="announce">Whether a successful enqueue raises <see cref="Enqueued"/>. Drag-and-drop
    /// imports pass false and report the outcome themselves (see <see cref="Import.DroppedFileImporter"/>):
    /// their message carries information the generic notification can't — the replay's player for a
    /// dropped .osr — and the two toasts would otherwise be drawn on top of each other.</param>
    public async Task EnqueueAndMaybePlayAsync(BeatmapSetInfo set, bool announce = true)
    {
        if (queue.Enqueue(set) && announce)
            Enqueued?.Invoke(set);

        // Fire-and-forget: start caching this set immediately rather than waiting for its turn
        // at the head of the queue, so a queued download is already underway (or done) by the
        // time advanceRoundAsync gets to it. Safe to call unconditionally — GetAsync's inflight
        // dict dedupes against both this and any later prefetch/advance-round call for the same
        // set id.
        prefetchInBackground(set.Id);

        // Advance not just when idle, but also when radio filler is currently playing: the user's
        // pick should interrupt that (SkipCurrent semantics, via AdvanceAsync's coalescing guard)
        // rather than wait for the radio track to finish. A set already playing from the queue is
        // left alone — later enqueues just wait their turn behind it.
        if (playback.Current.Value == null || currentIsRadio)
            await AdvanceAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The in-flight prefetches, so one can be called off when the set it was warming turns out not
    /// to be wanted after all.
    ///
    /// <para>
    /// A prefetch is an interested party in the shared download (see
    /// <see cref="BeatmapCache.GetAsync"/>), which is exactly right while the set is still coming
    /// up — it is what keeps a download alive when an advance abandons the same set. But it also
    /// means an abandoned song's download cannot actually stop while its prefetch is still waiting,
    /// so the prefetch has to be droppable too.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<int, CancellationTokenSource> prefetches = new();

    private async void prefetchInBackground(int setId)
    {
        var cancellation = new CancellationTokenSource();

        // A second prefetch of the same set replaces the first; the first is already sharing that
        // one download, so nothing is lost by letting the newer registration own the cancellation.
        prefetches[setId] = cancellation;

        try
        {
            await cache.GetAsync(setId, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Called off — see abandonPrefetch. Not a failure.
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Jukebox: prefetch of set {setId} failed");
        }
        finally
        {
            prefetches.TryRemove(new KeyValuePair<int, CancellationTokenSource>(setId, cancellation));
            cancellation.Dispose();
        }
    }

    /// <summary>Stops warming <paramref name="setId"/>. Called when a round gives up on a set, so
    /// the prefetch it started is not left holding that download open for a song nobody is waiting
    /// for any more.</summary>
    private void abandonPrefetch(int setId)
    {
        if (prefetches.TryRemove(setId, out var cancellation))
            cancellation.Cancel();
    }

    /// <summary>
    /// Skips the currently playing (or loading) track by advancing immediately.
    /// </summary>
    public void SkipCurrent() => AdvanceAsync();

    /// <summary>
    /// Jumps straight to <paramref name="set"/>, which must already be queued: it starts now, and
    /// the queue carries on from WHERE IT SAT rather than from the front. Everything that was ahead
    /// of it is dropped — the user skipped past those, exactly as if they had pressed next until
    /// they got here. This is a queue, not a playlist with a cursor; entries leave it as they play
    /// (see <see cref="MusicQueue.PopNext"/>), so "the ones you jumped over" have nowhere to wait.
    ///
    /// <para>
    /// Deliberately NOT "move this to the front": that would keep the skipped-over entries and play
    /// them straight after, which is the opposite of what clicking play on the fifth row means.
    /// </para>
    ///
    /// <para>
    /// Implemented as a queue edit plus an ordinary advance rather than a second play path, so it
    /// inherits everything a normal round already does — download with progress, cache eviction,
    /// failure fallback — and so the coalescing guard arbitrates it against an advance that is
    /// already running instead of the two racing to start different songs.
    /// </para>
    /// </summary>
    /// <remarks>Update thread only, same contract as <see cref="EnqueueAndMaybePlayAsync"/> and
    /// <see cref="SkipCurrent"/>: it touches <see cref="queue"/> directly.</remarks>
    /// <returns>Whether <paramref name="set"/> was queued and is now starting.</returns>
    public bool PlayNow(BeatmapSetInfo set)
    {
        int index = queue.Items.IndexOf(set);

        if (index < 0)
            return false;

        // Leaves `set` itself at the head for the advance round below to pop, so the entry is
        // consumed exactly once and by the usual path.
        if (index > 0)
            queue.Items.RemoveRange(0, index);

        AdvanceAsync();
        return true;
    }

    /// <summary>
    /// A veto on auto-advancing when the track finishes. Set by the screen when the result screen is
    /// enabled and the finished song was a replay: the result screen shows and playback HOLDS on it
    /// until the user advances (their Next click goes through <see cref="SkipCurrent"/>, which advances
    /// past this veto). Null — the default — always advances, the jukebox's normal roll-on behaviour.
    /// </summary>
    public Func<bool>? HoldOnTrackCompleted { get; set; }

    private void onTrackCompleted()
    {
        // The user asked to stop on a result screen for this play — don't roll on to the next song.
        if (HoldOnTrackCompleted?.Invoke() == true)
            return;

        AdvanceAsync();
    }

    /// <summary>
    /// Pops the next set to play (queue first, radio fallback), caches it and hands it to the
    /// <see cref="PlaybackController"/>. Internal (rather than private) so tests can drive it
    /// directly instead of waiting on a real track to finish playing.
    /// </summary>
    internal Task AdvanceAsync()
    {
        lock (advanceLock)
        {
            if (advancing)
            {
                // A round is already in flight — record that one more is wanted once it
                // finishes, instead of running (or losing) this request now…
                pendingAdvance = true;

                // …and tell that round to stop, so "once it finishes" is now rather than whenever
                // the download it no longer needs happens to complete.
                roundCancellation?.Cancel();
                return Task.CompletedTask;
            }

            advancing = true;
        }

        return advanceLoopAsync();
    }

    private async Task advanceLoopAsync()
    {
        try
        {
            while (true)
            {
                try
                {
                    await advanceRoundAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A round threw outside the failure handling advanceRoundAsync already does
                    // for cache lookups (e.g. an unexpected fault from RadioService or
                    // PlaybackController). Surface it the same way a handled failure is
                    // surfaced, and fall through to the pending-check below instead of
                    // rethrowing, so the guard still gets released rather than staying stuck
                    // "advancing" forever.
                    Logger.Error(ex, "Jukebox: advance round failed unexpectedly");
                    string message = ex.Message;
                    Schedule(() => LastError.Value = $"Unexpected error: {message}");
                }

                lock (advanceLock)
                {
                    if (pendingAdvance)
                    {
                        // A request coalesced while this round ran — consume it and run one more
                        // round without releasing the guard in between (so a request arriving in
                        // that gap can't be missed).
                        pendingAdvance = false;
                        continue;
                    }

                    advancing = false;
                    return;
                }
            }
        }
        finally
        {
            // Last-resort safety net: guarantees the guard can never wedge permanently even if
            // something above (e.g. the lock block itself) throws in a way the catch above
            // doesn't cover. Idempotent with the normal-path release above.
            lock (advanceLock)
                advancing = false;
        }
    }

    /// <summary>
    /// Drops the "still working on it" feedback — on the update thread, so every caller is already
    /// inside a <c>Schedule</c>. The two always move together: a percentage with no status line to
    /// attach it to (or a status line whose percentage keeps ticking after the round is over) would
    /// each read as a stuck download.
    /// </summary>
    private void clearStatus()
    {
        Status.Value = null;
        DownloadingSetId.Value = null;
    }

    /// <summary>
    /// Which advance round currently owns the status line. Every write goes through
    /// <see cref="showStatus"/>/<see cref="releaseStatus"/> carrying the round's own token,
    /// and a write whose token is stale does nothing.
    ///
    /// <para>
    /// This is what stops a dead download's title sitting under a different song. A round announces
    /// "Downloading X…" before fetching and clears it on each of its own exits — but an exception
    /// thrown anywhere after that announcement (a playback fault, the replay difficulty switch, the
    /// prefetch read) used to escape to the loop's handler, which reports an error and returns
    /// WITHOUT clearing. The next round then clears LastError, the panel falls back to Status, and
    /// the stale line reappears under whatever is playing by then — with nothing left that would
    /// ever clear it.
    /// </para>
    /// </summary>
    private int statusOwner;

    /// <summary>
    /// Which round's text is on the line right now, as opposed to which round is merely the newest.
    /// Update thread only — every read and write of it happens inside a <c>Schedule</c>.
    ///
    /// <para>
    /// The two are not the same thing, and conflating them is how a stale line survived: a round
    /// that has been superseded still has to be able to clear ITS OWN text, even though a newer
    /// round has taken ownership by the time the release runs. Keying the release on "did I write
    /// what is showing" instead of "am I still the newest" is what makes that work, while still
    /// preventing a late release from wiping a newer round's line.
    /// </para>
    /// </summary>
    private int statusShownBy;

    /// <summary>
    /// Puts <paramref name="text"/> on the status line on this round's behalf.
    /// </summary>
    /// <param name="owner">The round's status token.</param>
    /// <param name="text">The line to show.</param>
    /// <param name="setId">The set whose download progress the UI should poll and append as a
    /// percentage, or null for a phase with no byte progress to report (the lookup, which hasn't
    /// picked a set yet; the prepare, whose bytes are already down). The spinner runs for all of
    /// them — it keys off the line existing, not off this.</param>
    private void showStatus(int owner, string text, int? setId) => Schedule(() =>
    {
        // A superseded round must not announce at all — by the time this runs, the round that
        // replaced it may already be showing its own line.
        if (owner != Volatile.Read(ref statusOwner))
            return;

        Status.Value = text;
        DownloadingSetId.Value = setId;
        statusShownBy = owner;
    });

    /// <summary>Clears the line only if <paramref name="owner"/> is what put the text there.</summary>
    private void releaseStatus(int owner) => Schedule(() =>
    {
        if (statusShownBy != owner)
            return;

        clearStatus();
        statusShownBy = 0;
    });

    private async Task advanceRoundAsync()
    {
        // Cleared once per round (not per pop-attempt below) so a round that eventually
        // succeeds ends with no stale error, while a failure encountered along the way to that
        // success is still visible for the rest of the round (see the failing-set test).
        //
        // EXCEPT while the same failure is still ongoing. Clearing and re-setting an identical
        // message is a value CHANGE either way, and every consumer of this bindable reacts to
        // changes — so the radio retrying against dead mirrors fired a fresh, identical toast on
        // every retry. Leaving the line untouched makes the re-announcement below a no-op, which
        // is what turns N toasts for one problem back into one.
        Schedule(() =>
        {
            if (ongoingFailure == null)
                LastError.Value = null;
        });

        // Taking ownership of the status line for this round. The finally below is the one thing
        // that guarantees it is handed back however this round ends — returning, falling out of the
        // loop, or throwing past every handler in it.
        int owner = Interlocked.Increment(ref statusOwner);

        var cancellation = new CancellationTokenSource();

        lock (advanceLock)
        {
            roundCancellation?.Dispose();
            roundCancellation = cancellation;
        }

        try
        {
            await advanceRoundBodyAsync(owner, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Superseded by a newer request while waiting on a download. Not an error and not worth
            // reporting: the round that request asked for runs next, immediately.
        }
        finally
        {
            releaseStatus(owner);

            // Safety net, in the same spirit as releaseStatus above: whatever happened in there —
            // an unexpected fault mid-search, a cancellation, an early return — the lookup is over
            // once the round is, and next must not be left permanently dead.
            setLookingUp(false);

            lock (advanceLock)
            {
                if (ReferenceEquals(roundCancellation, cancellation))
                    roundCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task advanceRoundBodyAsync(int owner, CancellationToken ct)
    {
        while (true)
        {
            // Routed through onUpdateThread: this loop's first iteration runs synchronously on
            // whatever thread called AdvanceAsync (the update thread, per every caller's
            // contract), but a `continue` below (a failed candidate) or a coalesced second round
            // from advanceLoopAsync always resumes after an awaited ConfigureAwait(false), i.e.
            // on the threadpool — popping straight off queue.Items there would race any UI bound
            // to its CollectionChanged (see onUpdateThread's doc comment).
            BeatmapSetInfo? fromQueue = await onUpdateThread(() => queue.PopNext()).ConfigureAwait(false);
            bool viaRadio = fromQueue == null;
            BeatmapSetInfo? next = fromQueue;

            if (next == null)
            {
                // Spend the launch permit (see radioAllowedOnce) before consulting the setting, so
                // it is consumed by the round it was granted for whether or not it was needed —
                // otherwise a permit issued at launch could survive to justify a lookup hours later.
                bool permitted = await onUpdateThread(() =>
                {
                    bool once = radioAllowedOnce;
                    radioAllowedOnce = false;
                    return once || RadioOnEmptyQueue.Value;
                }).ConfigureAwait(false);

                if (!permitted)
                {
                    // The radio is switched off and the queue is empty, so there is nothing to
                    // play and nothing has gone wrong. Deliberately silent: no status line, no
                    // error, and no retry timer — reporting "couldn't find a song" for a search
                    // that was never made is the exact opposite of what the setting asked for.
                    // The caller's finally releases the status line and the lookup flag.
                    return;
                }

                // The phase the user actually complained about: searching a mirror for a random
                // song takes seconds (longer still when mirrors are failing and RadioService works
                // through its retries), and it used to show nothing whatsoever — no line, no
                // spinner — so pressing next with an empty queue looked like a dead button.
                showStatus(owner, "Looking for a song…", null);
                setLookingUp(true);

                RadioPick pick;

                try
                {
                    pick = await radio.PickRandomAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    setLookingUp(false);
                }

                next = pick.Set;

                if (next == null)
                {
                    radioFailures++;

                    // Says WHY, where "No tracks available" said nothing a user could act on. The
                    // wording is deliberately FIXED — no countdown, no attempt number. Anything that
                    // varies per retry is a new value on the bindable, and a new value is a new
                    // toast: putting the (growing) delay in here reintroduced the very loop this
                    // round exists to remove, which is how the test below caught it.
                    announceFailure($"{pick.Failure} Retrying automatically.");
                    Scheduler.AddDelayed(() => AdvanceAsync(), radioRetryDelay);
                    return;
                }

                radioFailures = 0;

                // Nothing was reachable, so this came off the disk. Said once (announceFailure
                // collapses the repeats) because a radio that has quietly stopped reaching for new
                // music, with no explanation, reads as broken rather than as degraded.
                if (pick.FromCache)
                {
                    // Two different degradations, and the second is worth its own wording: the
                    // song is not merely older than the radio would have found, it is the wrong
                    // MODE, which silently looks like the mode filter having no effect at all.
                    announceFailure(pick.CacheFilterRelaxed
                        ? "Can't reach any beatmap source — playing from your cache, ignoring the radio's mode filter."
                        : "Can't reach any beatmap source — playing from your cache.");
                }
                else
                    clearOngoingFailure();
            }

            // Checked before GetAsync rather than having GetAsync report hit/miss: the cache can
            // only grow via a download, so gating eviction on "was this a miss" means a run of
            // cache hits (skip/track-complete with everything already cached) costs nothing —
            // no re-stat of every cached set's size on every round.
            bool wasCached = cache.IsCached(next.Id);

            if (!wasCached)
                showStatus(owner, $"Downloading {next.DisplayTitle}…", next.Id);

            CachedBeatmapSet cached;

            try
            {
                // The token is what makes a skip land immediately: it detaches this round from the
                // download without disturbing anyone else waiting on that same set (a prefetch for
                // something still queued keeps it alive — see BeatmapCache.GetAsync).
                cached = await cache.GetAsync(next.Id, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Superseded. Rethrown rather than reported: the caller's handler treats it as a
                // clean exit, and the round the user actually asked for runs next. The prefetch
                // goes too — while it is still waiting, the download it shares cannot stop.
                abandonPrefetch(next.Id);
                throw;
            }
            catch (Exception ex)
            {
                string message = $"Failed to load '{next.DisplayTitle}': {ex.Message}";
                Schedule(() => LastError.Value = message);
                releaseStatus(owner);
                continue;
            }

            // Checked again after the download: a skip that arrives during the extract, or in the
            // gap before playback starts, must still stop this song reaching the speakers. Without
            // this the song being skipped got its moment of playback anyway.
            if (ct.IsCancellationRequested)
            {
                abandonPrefetch(next.Id);
                ct.ThrowIfCancellationRequested();
            }

            // The gap between a download finishing and audio starting is the extract plus the
            // track load — seconds for a large set, and previously as silent as the lookup was:
            // the "Downloading…" line sat at 100% throughout, reading as a stuck download. Only
            // announced for a round that actually downloaded; a cache hit reaches playback fast
            // enough that a line here would just flicker (see Status's own doc).
            if (!wasCached)
                showStatus(owner, $"Preparing {next.DisplayTitle}…", null);

            // Set BEFORE the track starts, and set on every round (not just replay rounds), so the
            // rate a previous replay forced can never leak into the next song — this one write is
            // the single point of control for it. Tempo and frequency are carried separately
            // because osu!'s rate mods disagree about pitch (DT preserves it, NC raises it).
            double replayTempo = next.Replay?.RateTempo ?? 1;
            double replayFrequency = next.Replay?.RateFrequency ?? 1;

            Schedule(() =>
            {
                playback.ReplayTempo.Value = replayTempo;
                playback.ReplayFrequency.Value = replayFrequency;
            });

            bool played = await playback.PlayAsync(cached).ConfigureAwait(false);

            if (!played)
            {
                // No loadable audio for this set (e.g. AudioFilename missing/points at a file
                // that doesn't exist, or the track failed to decode). Counting this round a
                // success would leave nothing playing and TrackCompleted never firing again —
                // treat it exactly like a cache/download failure above: report and try the next
                // candidate instead of wedging.
                string message = $"No playable audio for '{next.DisplayTitle}'";
                Schedule(() => LastError.Value = message);
                releaseStatus(owner);
                continue;
            }

            // A set carrying a dropped replay must play the EXACT difficulty that replay was
            // recorded on — identified by checksum at import time — not the set's default one,
            // which is generally a different diff entirely. Done after PlayAsync rather than
            // instead of it so the normal path stays untouched; SwitchDifficultyAsync keeps the
            // clock running and reloads the track only if that difficulty uses different audio.
            string? replayDiff = next.Replay?.OsuFile;

            if (replayDiff != null && replayDiff != cached.PreferredOsuFile && cached.OsuFiles.Contains(replayDiff))
                await playback.SwitchDifficultyAsync(replayDiff).ConfigureAwait(false);

            Schedule(() =>
            {
                NowPlaying.Value = next;
                currentIsRadio = viaRadio;

                // Cleared in the same callback as NowPlaying so the two never disagree for a frame —
                // a "downloading" line under a song that has already started reads as stuck. This
                // round is what is showing (it just finished its own download), so it owns the line.
                clearStatus();
                statusShownBy = 0;
            });

            // Fire-and-forget prefetch of the new queue head, so it's likely already cached
            // by the time we get to it. Deliberately not eviction-gated itself: if this downloads
            // a set, the cache stays over-limit until that set becomes "current" on some later
            // round and its own download triggers eviction — a self-correcting delay rather than
            // a second eviction path to reason about here.
            //
            // Read via onUpdateThread rather than touching queue.Items directly: this point is
            // always reached after the cache.GetAsync/PlayAsync awaits above, so we're on the
            // threadpool here regardless of which loop iteration this is.
            int? headId = await onUpdateThread(() => queue.Items.Count > 0 ? queue.Items[0].Id : (int?)null).ConfigureAwait(false);
            if (headId != null)
                _ = cache.GetAsync(headId.Value);

            if (!wasCached)
                evictCacheInBackground(next.Id);

            return;
        }
    }

    /// <summary>
    /// Runs <see cref="BeatmapCache.EvictToLimit"/> off the update thread (it enumerates, sizes
    /// and deletes set directories synchronously) so it never blocks playback. Fire-and-forget
    /// (async void, matching <see cref="prefetchInBackground"/>) with its own try/catch:
    /// eviction failures are logged but must never surface as a Jukebox failure, since the
    /// advance round that triggered this has already succeeded.
    /// </summary>
    private async void evictCacheInBackground(int currentId)
    {
        long limit = CacheLimitBytes;

        try
        {
            // Snapshot protected ids (current + queued) via onUpdateThread before hopping off it
            // — this method is called from advanceRoundAsync after its cache.GetAsync/PlayAsync
            // awaits, i.e. already on the threadpool, so touching the BindableList directly here
            // would race any UI bound to its CollectionChanged.
            var protectedIds = await onUpdateThread(() =>
            {
                var ids = new List<int> { currentId };
                ids.AddRange(queue.Items.Select(i => i.Id));
                return ids;
            }).ConfigureAwait(false);

            await Task.Run(() => cache.EvictToLimit(limit, protectedIds)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Jukebox: cache eviction failed");
        }
    }
}
