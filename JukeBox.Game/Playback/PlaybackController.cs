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
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Timing;

namespace JukeBox.Game.Playback;

public partial class PlaybackController : Component
{
    public readonly Bindable<CachedBeatmapSet?> Current = new();

    /// <summary>
    /// The .osu file (difficulty) whose visuals/chart/hitsounds should be shown for
    /// <see cref="Current"/>. Reset to the set's <see cref="CachedBeatmapSet.PreferredOsuFile"/>
    /// on every successful <see cref="PlayAsync"/> swap; changed by
    /// <see cref="SwitchDifficultyAsync"/> without interrupting playback.
    /// </summary>
    public readonly Bindable<string?> SelectedOsuFile = new();

    public readonly BindableDouble Volume = new(1) { MinValue = 0, MaxValue = 1 };

    /// <summary>
    /// Playback speed multiplier (0.1×–2.5×, default 1×), applied as a Tempo adjustment — speed
    /// changes without pitch shift, matching lazer's replay-player playback control. Session-only
    /// (not persisted), same as lazer. Visuals need no extra wiring: the storyboard and chart run
    /// off <see cref="PlaybackClock"/>, whose source is the tempo-adjusted track, so gameplay time
    /// follows the audible rate automatically.
    ///
    /// <para>
    /// The range is deliberately wider than lazer's replay speeds: this is a storyboard viewer, so
    /// crawling through a section at 0.1× is a real use, and 2.5× is the top end BASS's tempo
    /// shifter still resolves cleanly at.
    /// </para>
    /// </summary>
    public readonly BindableDouble PlaybackRate = new(1) { MinValue = 0.1, MaxValue = 2.5, Precision = 0.05 };

    /// <summary>
    /// Speed forced by the rate-changing mods of a replay being watched, split into its
    /// pitch-preserving half (<see cref="ReplayTempo"/>, DoubleTime/HalfTime) and its
    /// pitch-shifting half (<see cref="ReplayFrequency"/>, Nightcore/Daycore). Both sit at 1
    /// whenever no replay — or a replay with no rate mod — is playing. Set by <see cref="Jukebox"/>
    /// for each track it starts, from <see cref="Replays.ReplayMods.TrackAdjustmentsFor"/>.
    ///
    /// <para>
    /// The split is not cosmetic: osu!'s rate mods disagree about pitch. DoubleTime keeps it and
    /// Nightcore raises it; HalfTime keeps it and Daycore lowers it. Driving everything through
    /// frequency made DT sound chipmunked, which it never does in the real game.
    /// </para>
    ///
    /// <para>
    /// Either way this is what keeps a replay in sync rather than just looking fast: in osu! a rate
    /// mod moves the TRACK, and gameplay follows the track's clock — exactly this app's arrangement
    /// too (<see cref="PlaybackClock"/> sources from the track, and the storyboard and chart run off
    /// it). Both tempo and frequency move the track, so the clock follows either way; speeding the
    /// chart up on its own would have desynced it from music that never changed.
    /// </para>
    ///
    /// <para>
    /// Both are SEPARATE adjustments from <see cref="PlaybackRate"/>, not replacements for it: they
    /// multiply, so the user's own speed slider keeps working while a replay plays (a 1.5× DT replay
    /// at slider 0.5× runs at 0.75×) and is never moved or clobbered behind their back. The slider is
    /// "how fast do I want to watch this"; these are "how fast the play actually was".
    /// </para>
    /// </summary>
    public readonly BindableDouble ReplayTempo = new(1) { MinValue = 0.1, MaxValue = 3 };

    /// <summary>The pitch-shifting half of the replay's rate — see <see cref="ReplayTempo"/>.</summary>
    public readonly BindableDouble ReplayFrequency = new(1) { MinValue = 0.1, MaxValue = 3 };

    /// <summary>
    /// Speed forced by the rate-changing mods the user picked in the Chart tab (see
    /// <see cref="LazerPlayer.ChartModSelection"/>), split into pitch-preserving tempo (DT/HT) and
    /// pitch-shifting frequency (NC/DC) for exactly the reasons <see cref="ReplayTempo"/> is.
    ///
    /// <para>
    /// Deliberately SEPARATE bindables from the replay pair rather than a shared "forced rate":
    /// <see cref="Jukebox"/> rewrites the replay pair on every track it starts (so a previous
    /// replay's rate can never leak into the next song), which would wipe a user's own DT the
    /// moment the song changed. Being separate adjustments, all three multiply — the user's speed
    /// slider, the replay's rate, and the mod selection's rate coexist without any of them being
    /// clobbered. In practice the last two are mutually exclusive: while a replay drives playback
    /// the mod selection is inert and holds both of these at 1.
    /// </para>
    /// </summary>
    public readonly BindableDouble ChartModTempo = new(1) { MinValue = 0.1, MaxValue = 3 };

    /// <summary>The pitch-shifting half of the chart mods' rate — see <see cref="ChartModTempo"/>.</summary>
    public readonly BindableDouble ChartModFrequency = new(1) { MinValue = 0.1, MaxValue = 3 };

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

    // Absolute path of the audio file currentTrack was loaded from. Needed by
    // SwitchDifficultyAsync to decide whether a difficulty switch actually changes audio —
    // after a switch this can differ from Current.Value.AudioFile.
    private string? currentAudioPath;

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
        if (set.AudioFile == null && !set.HasVirtualAudio)
            return false;

        int myGeneration = Interlocked.Increment(ref generation);

        var (store, track) = set.AudioFile == null
            ? await loadVirtualTrackAsync(set, set.PreferredOsuFile).ConfigureAwait(false)
            : await loadFileTrackAsync(set.Directory, set.AudioFile).ConfigureAwait(false);

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
            currentAudioPath = set.AudioFile;
            Current.Value = set;
            SelectedOsuFile.Value = set.PreferredOsuFile;

            // AddAdjustment multiplies the track's own (default 1) volume by ours on every
            // change, including immediately — no separate initial-value assignment needed.
            track.AddAdjustment(AdjustableProperty.Volume, Volume);
            track.AddAdjustment(AdjustableProperty.Tempo, PlaybackRate);
            track.AddAdjustment(AdjustableProperty.Tempo, ReplayTempo);
            track.AddAdjustment(AdjustableProperty.Frequency, ReplayFrequency);
            track.AddAdjustment(AdjustableProperty.Tempo, ChartModTempo);
            track.AddAdjustment(AdjustableProperty.Frequency, ChartModFrequency);
            track.Completed += () => Schedule(() => TrackCompleted?.Invoke());

            decoupledClock.ChangeSource(track);
            decoupledClock.Start();

            previousTrack?.Dispose();
            previousStore?.Dispose();

            swapped.SetResult(true);
        });

        return await swapped.Task.ConfigureAwait(false);
    }

    private Task<(ITrackStore Store, Track? Track)> loadFileTrackAsync(string directory, string audioFile)
    {
        string fileName = Path.GetFileName(audioFile);

        return Task.Run(() =>
        {
            var s = audio.GetTrackStore(new StorageBackedResourceStore(new NativeStorage(directory, host)));
            return (Store: s, Track: (Track?)s.Get(fileName));
        });
    }

    /// <summary>
    /// The track for a keysound-only set (<see cref="CachedBeatmapSet.HasVirtualAudio"/>): a
    /// silent track whose only job is to be a clock of the right length. Everything downstream —
    /// the storyboard, the chart, seeking, the rate adjustments, <see cref="TrackCompleted"/> —
    /// runs off it exactly as it does off a real one, which is why this is a track rather than a
    /// bespoke clock: no other code path has to know the difference. The sound itself comes
    /// entirely from the chart's keysounds and the storyboard's <c>Sample</c> events.
    /// </summary>
    private Task<(ITrackStore Store, Track? Track)> loadVirtualTrackAsync(CachedBeatmapSet set, string? osuFile)
        => Task.Run(() =>
        {
            // Read off the update thread with the rest of the load: this parses the whole .osu
            // (and .osb) looking for the last hit object and storyboard event.
            double length = BeatmapDurationScanner.ComputeLength(set, osuFile);

            Logger.Log($"Virtual audio: '{Path.GetFileName(osuFile) ?? set.Directory}' has no music file — playing a {length / 1000:0.#}s silent track (keysounds and storyboard samples supply the audio)");

            // A store of its own, exactly as the file path builds one, so the swap below can
            // dispose it alongside its track like any other. Its backing storage is never read
            // from — GetVirtual synthesises the track — but it must not be null: the no-argument
            // overload hands back the GLOBAL track store, and disposing that on the next swap
            // takes every subsequent track in the app down with it.
            var s = audio.GetTrackStore(new StorageBackedResourceStore(new NativeStorage(set.Directory, host)));
            return (Store: s, Track: (Track?)s.GetVirtual(length, $"virtual:{set.SetId}"));
        });

    /// <summary>
    /// Switches the selected difficulty of the currently playing set WITHOUT restarting playback:
    /// the clock keeps running, so visuals rebuilt off <see cref="SelectedOsuFile"/> continue at
    /// the current time. When the new difficulty uses a different AudioFilename, the new track is
    /// loaded and seeked to the previous position (pause state preserved).
    /// </summary>
    public async Task<bool> SwitchDifficultyAsync(string osuPath)
    {
        var set = Current.Value;
        if (set == null || !set.OsuFiles.Contains(osuPath))
            return false;

        // Resolve the difficulty's audio file (metadata was captured at cache-load time).
        string? newAudio = null;
        var diff = set.Difficulties.Find(d => d.Path == osuPath);

        if (diff?.AudioFilename != null)
        {
            string candidate = Path.Combine(Path.GetDirectoryName(osuPath) ?? set.Directory, diff.AudioFilename);
            if (File.Exists(candidate))
                newAudio = candidate;
        }

        // Same audio (or no resolvable audio — keep whatever's playing): just retarget visuals.
        if (newAudio == null || string.Equals(Path.GetFullPath(newAudio), currentAudioPath != null ? Path.GetFullPath(currentAudioPath) : null, StringComparison.OrdinalIgnoreCase))
        {
            // A keysound-only set has no per-difficulty track to swap in, but its silent track's
            // LENGTH is difficulty-derived — a longer difficulty would otherwise be cut off where
            // the previous one ended. Grown, never shrunk, so the switch can't strand the clock
            // past the end of its own track. Computed here rather than inside the schedule
            // callback below: it reads the .osu off disk.
            double? grownLength = null;

            if (set.HasVirtualAudio)
                grownLength = await Task.Run(() => BeatmapDurationScanner.ComputeLength(set, osuPath)).ConfigureAwait(false);

            var selected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Schedule(() =>
            {
                bool stillCurrent = Current.Value == set;
                if (stillCurrent)
                {
                    if (grownLength > currentTrack?.Length)
                        currentTrack.Length = grownLength.Value;

                    SelectedOsuFile.Value = osuPath;
                }

                selected.SetResult(stillCurrent);
            });
            return await selected.Task.ConfigureAwait(false);
        }

        // Different audio: load the new track and continue at the same position.
        int myGeneration = Interlocked.Increment(ref generation);

        var (store, track) = await loadFileTrackAsync(Path.GetDirectoryName(newAudio) ?? set.Directory, newAudio).ConfigureAwait(false);

        if (track == null)
        {
            store.Dispose();
            return false;
        }

        var swapped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Schedule(() =>
        {
            // A newer PlayAsync/SwitchDifficultyAsync happened while loading — drop this one.
            if (myGeneration != Volatile.Read(ref generation) || Current.Value != set)
            {
                track.Dispose();
                store.Dispose();
                swapped.SetResult(false);
                return;
            }

            double resumeTime = decoupledClock.CurrentTime;
            bool wasRunning = decoupledClock.IsRunning;

            var previousTrack = currentTrack;
            var previousStore = currentStore;

            currentTrack = track;
            currentStore = store;
            currentAudioPath = newAudio;

            track.AddAdjustment(AdjustableProperty.Volume, Volume);
            track.AddAdjustment(AdjustableProperty.Tempo, PlaybackRate);
            track.AddAdjustment(AdjustableProperty.Tempo, ReplayTempo);
            track.AddAdjustment(AdjustableProperty.Frequency, ReplayFrequency);
            track.AddAdjustment(AdjustableProperty.Tempo, ChartModTempo);
            track.AddAdjustment(AdjustableProperty.Frequency, ChartModFrequency);
            track.Completed += () => Schedule(() => TrackCompleted?.Invoke());

            decoupledClock.ChangeSource(track);
            decoupledClock.Seek(resumeTime);
            if (wasRunning)
                decoupledClock.Start();
            else
                decoupledClock.Stop();

            previousTrack?.Dispose();
            previousStore?.Dispose();

            SelectedOsuFile.Value = osuPath;
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

    /// <summary>Pauses if playing; nothing otherwise. The result screen uses it to hold a finished
    /// song in a state the transport reads truthfully (a completed track leaves the clock reporting
    /// "running" while nothing plays).</summary>
    public void Pause()
    {
        if (decoupledClock.IsRunning)
            decoupledClock.Stop();
    }

    /// <summary>Starts playing if not already; nothing otherwise.</summary>
    public void Play()
    {
        if (!decoupledClock.IsRunning)
            decoupledClock.Start();
    }

    /// <summary>
    /// Back to the top, keeping the play/pause state: a playing song restarts playing, a paused one
    /// sits paused at 0:00. Not a bare <c>Seek(0)</c> for the playing case: once a track has run to
    /// its end the clock still reports running while its source has stopped, and a seek alone then
    /// rewinds a track that never starts again (the position sat on 0:00 forever). Stopping first
    /// puts the clock in a state <see cref="DecouplingFramedClock.Start"/> actually acts on.
    /// </summary>
    public void Restart()
    {
        bool wasRunning = decoupledClock.IsRunning;

        decoupledClock.Stop();
        decoupledClock.Seek(0);

        if (wasRunning)
            decoupledClock.Start();
    }

    public void Seek(double ms) => decoupledClock.Seek(ms);

    /// <summary>Test-only access to the live track (JukeBox.Game.Tests has InternalsVisibleTo).</summary>
    internal Track? CurrentTrack => currentTrack;

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        currentTrack?.Dispose();
        currentStore?.Dispose();
    }
}
