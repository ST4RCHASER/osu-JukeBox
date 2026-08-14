#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.IO.Stores;

namespace JukeBox.Game.Playback;

/// <summary>
/// Plays the 30s beatmap-set preview clip (osu!'s <c>https://b.ppy.sh/preview/{setId}.mp3</c> —
/// NOTE: the server actually serves ogg despite the extension, which is why this goes through a
/// BASS-backed track store over an <see cref="OnlineStore"/> rather than anything
/// extension-driven; BASS sniffs the container from the bytes). Used by the fullscreen listing's
/// hover-expanded cards.
///
/// <para>
/// While a preview plays, the main jukebox track is PAUSED (not just ducked) and resumed when the
/// preview stops/completes — chosen over a volume duck as the simplest behaviour that can never
/// leave two tracks audible at once, and it round-trips cleanly through
/// <see cref="PlaybackController.TogglePause"/> without new playback machinery. The resume only
/// fires if this component was the one that paused (a track the user had already paused stays
/// paused).
/// </para>
///
/// <para>
/// Can never wedge the jukebox: every started preview track/store is disposed on the next
/// preview, on <see cref="Stop"/> (the owning overlay calls it on close) and on disposal, and a
/// stale async load (superseded while fetching) disposes its own track instead of swapping in
/// (<see cref="generation"/> guard, same shape as <see cref="PlaybackController.PlayAsync"/>).
/// </para>
/// </summary>
public partial class PreviewPlayer : Component
{
    /// <summary>The set id currently previewing, null when idle — cards bind this to flip their
    /// preview button between play and stop.</summary>
    public IBindable<int?> PlayingSetId => playingSetId;

    private readonly Bindable<int?> playingSetId = new Bindable<int?>();

    [Resolved]
    private AudioManager audio { get; set; } = null!;

    [Resolved(canBeNull: true)]
    private PlaybackController? playback { get; set; }

    /// <summary>
    /// Test seam (JukeBox.Game.Tests has InternalsVisibleTo): produces the (store, track) pair
    /// for a preview URL. The default fetches through a BASS track store over an
    /// <see cref="OnlineStore"/>; tests substitute a recorder returning a
    /// <see cref="TrackVirtual"/> so the URL contract and pause/resume logic are covered without
    /// the network.
    /// </summary>
    internal Func<string, (IDisposable? store, Track? track)> LoadTrack;

    private IDisposable? currentStore;
    private Track? currentTrack;

    /// <summary>Whether the main playback was running when this preview started — only then is it
    /// resumed on stop.</summary>
    private bool pausedMainForPreview;

    // Arbitrates overlapping Play calls, same shape as PlaybackController.PlayAsync: only the
    // most recent call's load may swap its track in; superseded loads dispose their own result.
    private int generation;

    public PreviewPlayer()
    {
        LoadTrack = url =>
        {
            // AudioManager retains every track store it hands out until disposed (see
            // PlaybackController.currentStore) — returned alongside the track so stop/next
            // preview disposes it too.
            var store = audio.GetTrackStore(new OnlineStore());
            return (store, store.Get(url));
        };
    }

    internal static string PreviewUrl(int setId) => $"https://b.ppy.sh/preview/{setId}.mp3";

    /// <summary>Starts (or restarts) the preview for <paramref name="setId"/>, replacing any
    /// current one. Toggling semantics live in the caller (see the card's preview button).</summary>
    public void Play(int setId)
    {
        int myGeneration = Interlocked.Increment(ref generation);

        string url = PreviewUrl(setId);

        _ = Task.Run(() => LoadTrack(url)).ContinueWith(t =>
        {
            var (store, track) = t.IsCompletedSuccessfully ? t.Result : (null, null);

            Schedule(() =>
            {
                // A newer Play/Stop superseded this load while it was fetching — drop it.
                if (myGeneration != Volatile.Read(ref generation) || track == null)
                {
                    track?.Dispose();
                    store?.Dispose();
                    return;
                }

                stopCurrent();

                currentTrack = track;
                currentStore = store;

                if (playback?.IsPlaying == true && !pausedMainForPreview)
                {
                    playback.TogglePause();
                    pausedMainForPreview = true;
                }

                track.Completed += () => Schedule(() =>
                {
                    // Only stop if this track is still the active preview (a newer preview may
                    // already have replaced it between the completion and this schedule).
                    if (currentTrack == track)
                        Stop();
                });

                track.Start();
                playingSetId.Value = setId;
            });
        });
    }

    /// <summary>Stops and disposes the current preview (and any in-flight load), resuming the
    /// main playback if this player was the one that paused it.</summary>
    public void Stop()
    {
        Interlocked.Increment(ref generation);
        stopCurrent();

        if (pausedMainForPreview)
        {
            pausedMainForPreview = false;

            if (playback is { IsPlaying: false })
                playback.TogglePause();
        }
    }

    private void stopCurrent()
    {
        currentTrack?.Dispose();
        currentTrack = null;
        currentStore?.Dispose();
        currentStore = null;
        playingSetId.Value = null;
    }

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        currentTrack?.Dispose();
        currentStore?.Dispose();
    }
}
