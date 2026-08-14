#nullable enable

using System.Collections.Concurrent;
using System.Threading.Tasks;
using osu.Framework.Graphics.Textures;

namespace JukeBox.Game.Online;

/// <summary>
/// Wraps the single, cached online <see cref="TextureStore"/> used for beatmap set cover
/// thumbnails (<c>https://b.ppy.sh/thumb/{setId}l.jpg</c>, osu! CDN covers). A dedicated wrapper
/// type — rather than caching the bare <see cref="TextureStore"/> directly — keeps this specific
/// store unambiguous to resolve even if some other <see cref="TextureStore"/> (e.g.
/// background/video textures elsewhere) is ever cached in the same dependency tree, and gives
/// test scenes that don't wire one up an explicit, self-documenting
/// <c>[Resolved(canBeNull: true)]</c> seam.
///
/// <para>
/// Fetch through <see cref="GetAsync"/>, never <c>TextureStore.GetAsync</c> directly:
/// the framework store's own duplicate-lookup handling calls <c>WaitSafely</c> from the thread
/// pool thread <c>TextureStore.GetAsync</c> runs on, which THROWS
/// (<c>InvalidOperationException: Can't use WaitSafely from inside an async operation</c>)
/// whenever two async lookups for the SAME url overlap. With both listing presentations
/// rendering cards for the same result sets (and rebuilds re-requesting covers while earlier
/// fetches are still in flight), same-url overlap is routine — so this wrapper deduplicates:
/// concurrent requests for one url share a single underlying store lookup, and once it
/// completes, later requests hit the store's own texture cache synchronously.
/// </para>
/// </summary>
public class OnlineThumbnailStore
{
    public required TextureStore Store { get; init; }

    private readonly ConcurrentDictionary<string, Task<Texture?>> inFlight = new ConcurrentDictionary<string, Task<Texture?>>();

    /// <summary>Fetches <paramref name="url"/>, sharing one underlying lookup between all
    /// concurrent requests for the same url (see the class summary). The returned texture is null
    /// when the resource doesn't exist.</summary>
    public Task<Texture?> GetAsync(string url)
    {
        var task = inFlight.GetOrAdd(url, fetchAsync);

        // Drop the in-flight entry once done (idempotent — registering this continuation more
        // than once for shared tasks is harmless): completed lookups live in the store's own
        // texture cache, so later calls resolve there instead of pinning Task objects here.
        task.ContinueWith(_ => inFlight.TryRemove(url, out Task<Texture?>? _), TaskContinuationOptions.ExecuteSynchronously);

        return task;
    }

    private async Task<Texture?> fetchAsync(string url) => await Store.GetAsync(url, System.Threading.CancellationToken.None).ConfigureAwait(false);
}
