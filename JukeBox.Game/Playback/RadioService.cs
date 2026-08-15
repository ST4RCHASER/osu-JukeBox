#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;

namespace JukeBox.Game.Playback;

public class RadioService
{
    private const int maxAttempts = 3;

    private static readonly string[] sorts =
    {
        "ranked_desc", "ranked_asc", "favourites_desc", "plays_desc", "updated_desc"
    };

    private readonly IBeatmapMirror mirror;
    private readonly Func<int, int, int> rng;

    public RadioService(IBeatmapMirror mirror, Func<int, int, int>? rng = null)
    {
        this.mirror = mirror;
        this.rng = rng ?? Random.Shared.Next;
    }

    public async Task<BeatmapSetInfo?> PickRandomAsync(CancellationToken ct = default)
    {
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var request = new SearchRequest
                {
                    Page = rng(0, 200),
                    PageSize = 50,
                    Status = "ranked",
                    Sort = sorts[rng(0, sorts.Length)]
                };

                var results = await mirror.SearchAsync(request, ct).ConfigureAwait(false);
                var candidates = results.Where(s => !s.DownloadDisabled).ToList();
                if (candidates.Count == 0) continue;

                return candidates[rng(0, candidates.Count)];
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Being called off is not a failed attempt. Without this the blanket catch below
                // swallowed it and burned the remaining retries on a search nobody is waiting for
                // any more — so a caller that cancels (an advance round superseded mid-lookup) both
                // waited out the retries and got a null "no tracks available" for its trouble.
                throw;
            }
            catch
            {
                // failed attempt; try again
            }
        }

        return null;
    }
}
