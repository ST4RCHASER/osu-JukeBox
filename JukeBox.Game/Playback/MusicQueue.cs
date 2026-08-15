#nullable enable

using System.Linq;
using JukeBox.Game.Online;
using osu.Framework.Bindables;

namespace JukeBox.Game.Playback;

public class MusicQueue
{
    public readonly BindableList<BeatmapSetInfo> Items = new();

    /// <summary>
    /// Appends <paramref name="set"/> unless a set with the same id is already queued. Returns
    /// whether it was actually added, so a caller can tell a real enqueue apart from a duplicate
    /// that changed nothing (see <see cref="Jukebox.Enqueued"/>, which must not announce the
    /// latter).
    /// </summary>
    public bool Enqueue(BeatmapSetInfo set)
    {
        if (Items.Any(i => i.Id == set.Id)) return false;

        Items.Add(set);
        return true;
    }

    public BeatmapSetInfo? PopNext()
    {
        if (Items.Count == 0) return null;

        var next = Items[0];
        Items.RemoveAt(0);
        return next;
    }
}
