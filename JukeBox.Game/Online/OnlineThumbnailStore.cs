#nullable enable

using osu.Framework.Graphics.Textures;

namespace JukeBox.Game.Online;

/// <summary>
/// Wraps the single, cached online <see cref="TextureStore"/> used for beatmap set cover
/// thumbnails (<c>https://b.ppy.sh/thumb/{setId}l.jpg</c>). A dedicated wrapper type — rather
/// than caching the bare <see cref="TextureStore"/> directly — keeps this specific store
/// unambiguous to resolve even if some other <see cref="TextureStore"/> (e.g. background/video
/// textures elsewhere) is ever cached in the same dependency tree, and gives test scenes that
/// don't wire one up an explicit, self-documenting <c>[Resolved(canBeNull: true)]</c> seam.
/// </summary>
public class OnlineThumbnailStore
{
    public required TextureStore Store { get; init; }
}
