#nullable enable

using System;
using System.IO;
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

    protected override void Update()
    {
        base.Update();
        decoupledClock.ProcessFrame();
    }

    public async Task PlayAsync(CachedBeatmapSet set)
    {
        if (set.AudioFile == null)
            return;

        string directory = set.Directory;
        string fileName = Path.GetFileName(set.AudioFile);

        var track = await Task.Run(() =>
        {
            var store = audio.GetTrackStore(new StorageBackedResourceStore(new NativeStorage(directory, host)));
            return store.Get(fileName);
        }).ConfigureAwait(false);

        if (track == null)
            return;

        Schedule(() =>
        {
            var previous = currentTrack;

            currentTrack = track;
            Current.Value = set;

            // AddAdjustment multiplies the track's own (default 1) volume by ours on every
            // change, including immediately — no separate initial-value assignment needed.
            track.AddAdjustment(AdjustableProperty.Volume, Volume);
            track.Completed += () => Schedule(() => TrackCompleted?.Invoke());

            decoupledClock.ChangeSource(track);
            decoupledClock.Start();

            previous?.Dispose();
        });
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
    }
}
