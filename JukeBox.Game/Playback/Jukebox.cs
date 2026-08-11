#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Online;
using osu.Framework.Bindables;
using osu.Framework.Graphics;

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

    // Reentrancy guard: TrackCompleted, SkipCurrent and Start can all race to call Advance
    // concurrently (e.g. a manual skip landing right as the track naturally completes). Rather
    // than queueing overlapping requests, a plain 0/1 flag makes an Advance call that finds one
    // already in flight a no-op — the in-flight run will itself pick up whatever the queue looks
    // like by the time it gets there, so nothing is lost, only coalesced.
    private int advancing;

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
        if (Interlocked.CompareExchange(ref advancing, 1, 0) != 0)
            return Task.CompletedTask;

        return advanceCoreAsync();
    }

    private async Task advanceCoreAsync()
    {
        try
        {
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
                    // LastError is sticky (not cleared on a later success) — it reports the most
                    // recent failure encountered while conducting, not "current status".
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
        finally
        {
            Volatile.Write(ref advancing, 0);
        }
    }
}
