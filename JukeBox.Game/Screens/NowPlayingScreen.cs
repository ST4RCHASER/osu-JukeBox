#nullable enable

using JukeBox.Game.Beatmaps;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Screens;

namespace JukeBox.Game.Screens;

/// <summary>
/// Hosts the currently-playing beatmap's visual stack (<see cref="BeatmapVisuals"/>), swapping
/// it in as <see cref="PlaybackController.Current"/> changes. The outgoing stack is disposed only
/// once the incoming one has finished loading, so there's no visible gap on swap.
/// </summary>
public partial class NowPlayingScreen : Screen
{
    /// <summary>
    /// Test-only access to the currently-hosted visuals (JukeBox.Game.Tests has
    /// InternalsVisibleTo), to assert load/dispose lifecycle that isn't otherwise observable.
    /// </summary>
    internal BeatmapVisuals? CurrentVisuals { get; private set; }

    [Resolved]
    private PlaybackController playbackController { get; set; } = null!;

    // Arbitrates overlapping swaps the same way PlaybackController.PlayAsync arbitrates
    // overlapping PlayAsync calls: only the async load whose generation is still current when it
    // completes is allowed to swap in.
    private int generation;

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        playbackController.Current.BindValueChanged(onCurrentChanged, true);
    }

    private void onCurrentChanged(ValueChangedEvent<CachedBeatmapSet?> change)
    {
        int myGeneration = ++generation;

        if (change.NewValue == null)
        {
            swapIn(myGeneration, null);
            return;
        }

        var visuals = new BeatmapVisuals(change.NewValue, playbackController.PlaybackClock)
        {
            RelativeSizeAxes = Axes.Both,
        };

        LoadComponentAsync(visuals, loaded => swapIn(myGeneration, loaded));
    }

    private void swapIn(int loadedGeneration, BeatmapVisuals? loaded)
    {
        // A newer Current change happened while this one was loading — drop this load rather
        // than swap a stale visual stack in over whatever the newer change has (or will) set.
        if (loadedGeneration != generation)
        {
            loaded?.Dispose();
            return;
        }

        var previous = CurrentVisuals;
        CurrentVisuals = loaded;

        if (loaded != null)
            AddInternal(loaded);

        if (previous != null)
            RemoveInternal(previous, true);
    }
}
