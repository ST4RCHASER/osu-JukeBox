#nullable enable

using System;
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
    public async Task EnqueueAndMaybePlayAsync(BeatmapSetInfo set)
    {
        queue.Enqueue(set);

        if (playback.Current.Value == null)
            await AdvanceAsync().ConfigureAwait(false);
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

    private async Task advanceRoundAsync()
    {
        // Cleared once per round (not per pop-attempt below) so a round that eventually
        // succeeds ends with no stale error, while a failure encountered along the way to that
        // success is still visible for the rest of the round (see the failing-set test).
        Schedule(() => LastError.Value = null);

        while (true)
        {
            BeatmapSetInfo? next = queue.PopNext() ?? await radio.PickRandomAsync().ConfigureAwait(false);

            if (next == null)
            {
                Schedule(() => LastError.Value = "No tracks available; retrying radio shortly.");
                Scheduler.AddDelayed(() => AdvanceAsync(), radio_retry_delay_ms);
                return;
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
                continue;
            }

            await playback.PlayAsync(cached).ConfigureAwait(false);

            // Fire-and-forget prefetch of the new queue head, so it's likely already cached
            // by the time we get to it.
            if (queue.Items.Count > 0)
            {
                int headId = queue.Items[0].Id;
                _ = cache.GetAsync(headId);
            }

            return;
        }
    }
}
