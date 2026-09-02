#nullable enable

using System.Diagnostics;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
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

    /// <summary>
    /// How long the last swap took, from the track changing to the new stack's first frame — the
    /// window during which the screen has nothing of the new song to show. Also logged.
    /// </summary>
    internal double LastSwapLatencyMs { get; private set; }

    /// <summary>Test hook: how many visual stacks this screen has actually built. A song change
    /// must cost exactly one, however many bindables moved to express it.</summary>
    internal int Builds { get; private set; }

    private long swapRequestedAt;

    private osu.Framework.Threading.ScheduledDelegate? pendingRebuild;

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
        playbackController.Current.BindValueChanged(_ => scheduleRebuild(), true);
        playbackController.SelectedOsuFile.BindValueChanged(_ => scheduleRebuild());
    }

    /// <summary>
    /// Coalesces the two bindables into ONE rebuild per change. They move as a pair on a track
    /// swap and not atomically: <see cref="PlaybackController.Current"/> lands first, at which point
    /// the difficulty selection still belongs to the outgoing set and this screen falls back to the
    /// incoming set's preferred difficulty — then <see cref="PlaybackController.SelectedOsuFile"/>
    /// lands and, whenever the chosen difficulty is not the preferred one, names a DIFFERENT file
    /// and rebuilds the whole stack a second time.
    ///
    /// <para>
    /// Measured on a storyboard-heavy set: two full builds per song change (two storyboard decodes,
    /// two beatmap conversions), the first of which is thrown away on arrival for being a
    /// generation behind — while both competed for the same load threads, so the one that counted
    /// arrived later. Waiting a frame costs nothing and builds once.
    /// </para>
    /// </summary>
    private void scheduleRebuild()
    {
        pendingRebuild?.Cancel();
        pendingRebuild = Schedule(rebuild);
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
        Builds++;

        // The track has ALREADY changed by the time this runs, so whatever is on screen belongs to
        // a song that has stopped playing. It goes now rather than when the incoming stack finishes
        // loading — that load is seconds long on a storyboard-heavy set, and every one of those
        // seconds used to show the previous song's storyboard and chart running over the new audio.
        // See BeatmapVisuals.Retire.
        CurrentVisuals?.Retire();

        swapRequestedAt = Stopwatch.GetTimestamp();

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

        // How long the screen had nothing of the new song to show. Logged because "the visuals lag
        // the audio" is exactly the kind of report that needs a number attached, and because this
        // is where a regression in load cost (a heavier storyboard decode, a slower chart build)
        // would show up first.
        LastSwapLatencyMs = (Stopwatch.GetTimestamp() - swapRequestedAt) * 1000.0 / Stopwatch.Frequency;

        Logger.Log($"Visuals swap: {LastSwapLatencyMs:0}ms from track change to the new stack's first frame"
                   + $" ({(loaded == null ? "nothing playing" : $"set {requestedSet?.SetId}")})");
    }
}
