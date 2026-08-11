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

    // The (set, difficulty) pair the most recent rebuild was requested for — Current and
    // SelectedOsuFile change as a pair on track swap, so without this the screen would rebuild
    // the same visual stack twice back to back.
    private CachedBeatmapSet? requestedSet;
    private string? requestedOsuFile;

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        playbackController.Current.BindValueChanged(_ => rebuild(), true);
        playbackController.SelectedOsuFile.BindValueChanged(_ => rebuild());
    }

    private void rebuild()
    {
        // BindValueChanged registrations on the controller's bindables outlive this screen (the
        // controller is app-lifetime); once the screen is disposed its callbacks must be inert.
        if (IsDisposed)
            return;

        var set = playbackController.Current.Value;

        // A SelectedOsuFile value from another set (transient state mid-swap, or stale) falls
        // back to the set's own preferred difficulty.
        string? selected = playbackController.SelectedOsuFile.Value;
        string? file = set != null && selected != null && set.OsuFiles.Contains(selected)
            ? selected
            : set?.PreferredOsuFile;

        if (set == requestedSet && file == requestedOsuFile)
            return;

        requestedSet = set;
        requestedOsuFile = file;

        int myGeneration = ++generation;

        if (set == null)
        {
            swapIn(myGeneration, null);
            return;
        }

        var visuals = new BeatmapVisuals(set, playbackController.PlaybackClock, file)
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
