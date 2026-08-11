#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osu.Framework.Timing;

namespace JukeBox.Game.Playback;

public partial class PlaybackController : Component
{
    public readonly Bindable<CachedBeatmapSet?> Current = new();
    public readonly BindableDouble Volume = new(1) { MinValue = 0, MaxValue = 1 };

    // Stable across the controller's lifetime: consumers hold onto this reference while the
    // underlying track (the actual clock source) is swapped out on every PlayAsync.
    private readonly DecouplingFramedClock decoupledClock = new() { AllowDecoupling = true };

    public IFrameBasedClock PlaybackClock => decoupledClock;

    public event Action? TrackCompleted;

    public bool IsPlaying => decoupledClock.IsRunning;
    public double CurrentTimeMs => decoupledClock.CurrentTime;
    public double LengthMs => currentTrack?.Length ?? 0;

    [Resolved]
    private AudioManager audio { get; set; } = null!;

    [Resolved]
    private GameHost host { get; set; } = null!;

    private Track? currentTrack;

    // The TrackStore created for currentTrack, kept only so it can be disposed alongside the
    // track it produced on the next swap. AudioManager.GetTrackStore retains every store it
    // hands out on the audio thread until disposed — never disposing this would leak one store
    // per song played, forever, for the app's whole lifetime.
    private ITrackStore? currentStore;

    // Arbitrates overlapping PlayAsync calls: each call claims the next generation at entry
    // (synchronously, before the async load), and only the call whose generation is still
    // current when its load finishes is allowed to swap in its track. This guarantees the
    // most-recently-requested call always wins the swap, regardless of load completion order.
    private int generation;

    protected override void Update()
    {
        base.Update();
        decoupledClock.ProcessFrame();
    }

    // virtual: lets JukeboxTest inject a genuinely-throwing test double (PlaybackController's own
    // real failure paths return false rather than throw — see TestSceneJukebox — so this is
    // the only realistic seam for a test that exercises Jukebox's unhandled-exception guard).
    //
    // Returns whether playback was actually started: false when there's no audio file to play,
    // or the track failed to load (e.g. an unsupported/corrupt audio file) — either way Jukebox
    // must not treat the round as a success, or it wedges silently with nothing playing and
    // TrackCompleted never firing again.
    public virtual async Task<bool> PlayAsync(CachedBeatmapSet set)
    {
        if (set.AudioFile == null)
            return false;

        int myGeneration = Interlocked.Increment(ref generation);

        string directory = set.Directory;
        string fileName = Path.GetFileName(set.AudioFile);

        var (store, track) = await Task.Run(() =>
        {
            var s = audio.GetTrackStore(new StorageBackedResourceStore(new NativeStorage(directory, host)));
            return (store: s, track: s.Get(fileName));
        }).ConfigureAwait(false);

        if (track == null)
        {
            store.Dispose();
            return false;
        }

        // Bridges the scheduled swap (which must run on the update thread) back to this async
        // call's caller: the load succeeding isn't enough to report success — whether this call's
        // track actually became current (vs. being dropped by a newer overlapping call) is only
        // known once the scheduled callback below runs.
        var swapped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Schedule(() =>
        {
            // A newer PlayAsync call was made while this one was loading — drop this load
            // rather than swap a stale track in over whatever the newer call has (or will) set.
            if (myGeneration != Volatile.Read(ref generation))
            {
                track.Dispose();
                store.Dispose();
                swapped.SetResult(false);
                return;
            }

            var previousTrack = currentTrack;
            var previousStore = currentStore;

            currentTrack = track;
            currentStore = store;
            Current.Value = set;

            // AddAdjustment multiplies the track's own (default 1) volume by ours on every
            // change, including immediately — no separate initial-value assignment needed.
            track.AddAdjustment(AdjustableProperty.Volume, Volume);
            track.Completed += () => Schedule(() => TrackCompleted?.Invoke());

            decoupledClock.ChangeSource(track);
            decoupledClock.Start();

            previousTrack?.Dispose();
            previousStore?.Dispose();

            swapped.SetResult(true);
        });

        return await swapped.Task.ConfigureAwait(false);
    }

    public void TogglePause()
    {
        if (decoupledClock.IsRunning)
            decoupledClock.Stop();
        else
            decoupledClock.Start();
    }

    public void Stop()
    {
        decoupledClock.Stop();
        decoupledClock.Seek(0);
    }

    public void Seek(double ms) => decoupledClock.Seek(ms);

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        currentTrack?.Dispose();
        currentStore?.Dispose();
    }
}
