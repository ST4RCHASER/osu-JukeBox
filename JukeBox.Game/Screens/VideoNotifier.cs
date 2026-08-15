#nullable enable

using osu.Framework.Bindables;

namespace JukeBox.Game.Screens;

/// <summary>
/// Carries "this beatmap's video can't be played" from the per-song visual stack to whatever shows
/// notices, and remembers which set it has already said it about.
///
/// <para>
/// It exists as its own object for two reasons. The visual stack is rebuilt per song AND per
/// difficulty (see <see cref="NowPlayingScreen"/>, which builds a fresh <see cref="BeatmapVisuals"/>
/// whenever the set or the selected .osu changes), so nothing inside it can remember what the user
/// has already been told — switching difficulty on a video-less-video map would announce it again.
/// Keeping the memory here, keyed by set, is what makes the notice once-per-song rather than
/// once-per-rebuild.
/// </para>
///
/// <para>
/// It is also the reason the DETACHED player stays quiet: this is cached by
/// <see cref="MainScreen"/>, which only the master window has. The viewer process builds its own
/// visual stack, resolves no notifier, and so reports nothing — the notice belongs in the window
/// the user is actually interacting with, and the master keeps its visuals loaded while detached
/// (detaching only drops their Alpha), so it still has a stack to report from.
/// </para>
/// </summary>
public class VideoNotifier
{
    /// <summary>
    /// The latest notice. UI binds to this and turns each new value into a toast, matching how
    /// <see cref="Import.DroppedFileImporter.Notification"/> is consumed.
    /// </summary>
    public readonly Bindable<string?> Notice = new Bindable<string?>();

    /// <summary>The set the current notice was raised for, so a rebuild of the same set is silent.</summary>
    private int? announcedFor;

    /// <summary>
    /// Reports that <paramref name="setId"/> declares a video that cannot be played. Repeat calls
    /// for the same set do nothing — including re-raising the same text, which would otherwise be a
    /// fresh value on the bindable and so a fresh toast.
    /// </summary>
    public void ReportUnplayableVideo(int setId)
    {
        if (announcedFor == setId)
            return;

        announcedFor = setId;

        // Cleared first because the message is deliberately generic: the NEXT beatmap with a broken
        // video raises the identical string, and a bindable set to the value it already holds
        // reports no change at all — so the second beatmap's notice would be silently swallowed.
        // Consumers ignore the null (see MainScreen), so this is still one toast per beatmap.
        Notice.Value = null;
        Notice.Value = "This beatmap's video can't be played — showing the background instead.";
    }
}
