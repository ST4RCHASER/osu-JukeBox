#nullable enable

using System.Linq;
using JukeBox.Game.Online;
using osu.Framework.Bindables;

namespace JukeBox.Game.Playback;

public class MusicQueue
{
    public readonly BindableList<BeatmapSetInfo> Items = new();

    public void Enqueue(BeatmapSetInfo set)
    {
        if (Items.Any(i => i.Id == set.Id)) return;
        Items.Add(set);
    }

    public BeatmapSetInfo? PopNext()
    {
        if (Items.Count == 0) return null;

        var next = Items[0];
        Items.RemoveAt(0);
        return next;
    }
}
