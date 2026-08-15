#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
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
    private const double radio_retry_delay_ms = 5000;

    public readonly Bindable<string?> LastError = new();

    /// <summary>
    /// The <see cref="BeatmapSetInfo"/> metadata (title/artist/etc.) for whatever
    /// <see cref="PlaybackController"/> is currently playing — set alongside <see cref="PlaybackController.PlayAsync"/>
    /// in each advance round, so UI (e.g. a now-playing bar) has a title/artist to display without
    /// having to derive it from <see cref="Beatmaps.CachedBeatmapSet"/>, which carries none.
    /// </summary>
    public readonly Bindable<BeatmapSetInfo?> NowPlaying = new();

    /// <summary>
    /// Human-readable progress feedback for whatever the current advance round is doing that
    /// isn't instant — currently just "Downloading {title}…" while a cache-miss download is in
    /// flight. Null the rest of the time (including while the round is otherwise busy but nothing
    /// needs to download). UI (e.g. <see cref="UI.NowPlayingPanel"/>) shows this so a first-run
    /// download doesn't look like the app hung with no feedback at all.
    /// </summary>
    public readonly Bindable<string?> Status = new();

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
    }

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
    /// Kicks off playback if nothing has played yet (queue if non-empty, radio otherwise).
    /// A no-op once something is already playing.
    /// </summary>
    public void Start()
    {
        if (playback.Current.Value == null)
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

    private async void prefetchInBackground(int setId)
    {
        try
        {
            await cache.GetAsync(setId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Jukebox: prefetch of set {setId} failed");
        }
    }

    /// <summary>
    /// Skips the currently playing (or loading) track by advancing immediately.
    /// </summary>
    public void SkipCurrent() => AdvanceAsync();

    private void onTrackCompleted() => AdvanceAsync();

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
                // finishes, instead of running (or losing) this request now.
                pendingAdvance = true;
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

    private async Task advanceRoundAsync()
    {
        // Cleared once per round (not per pop-attempt below) so a round that eventually
        // succeeds ends with no stale error, while a failure encountered along the way to that
        // success is still visible for the rest of the round (see the failing-set test).
        Schedule(() => LastError.Value = null);

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
            BeatmapSetInfo? next = fromQueue ?? await radio.PickRandomAsync().ConfigureAwait(false);

            if (next == null)
            {
                Schedule(() => LastError.Value = "No tracks available; retrying radio shortly.");
                Scheduler.AddDelayed(() => AdvanceAsync(), radio_retry_delay_ms);
                return;
            }

            // Checked before GetAsync rather than having GetAsync report hit/miss: the cache can
            // only grow via a download, so gating eviction on "was this a miss" means a run of
            // cache hits (skip/track-complete with everything already cached) costs nothing —
            // no re-stat of every cached set's size on every round.
            bool wasCached = cache.IsCached(next.Id);

            if (!wasCached)
            {
                string downloadingTitle = next.DisplayTitle;
                int downloadingId = next.Id;
                Schedule(() =>
                {
                    Status.Value = $"Downloading {downloadingTitle}…";
                    DownloadingSetId.Value = downloadingId;
                });
            }

            CachedBeatmapSet cached;

            try
            {
                cached = await cache.GetAsync(next.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                string message = $"Failed to load '{next.DisplayTitle}': {ex.Message}";
                Schedule(() => LastError.Value = message);
                Schedule(clearStatus);
                continue;
            }

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
                Schedule(clearStatus);
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
                clearStatus();
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
