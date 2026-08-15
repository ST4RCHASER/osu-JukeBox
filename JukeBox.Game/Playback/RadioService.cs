#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Online;
using osu.Framework.Bindables;
using osu.Framework.Logging;

namespace JukeBox.Game.Playback
{
    /// <summary>
    /// The outcome of one radio pick. A bare null could not say WHY nothing came back, and the
    /// difference matters to the user: "every beatmap source is unreachable" is a network problem
    /// they can wait out, while "the search came back empty" is not.
    /// </summary>
    /// <param name="Set">The pick, or null when none could be made.</param>
    /// <param name="Failure">Why not, phrased for display. Null when <paramref name="Set"/> is set.</param>
    /// <param name="FromCache">Whether this came from the local cache because no source was
    /// reachable — the caller says so once, since silently playing only cached music would look
    /// like the radio had mysteriously narrowed.</param>
    public record RadioPick(BeatmapSetInfo? Set, string? Failure = null, bool FromCache = false);

    /// <summary>
    /// Picks a random beatmap set for the jukebox to play when the queue is empty.
    ///
    /// <para>
    /// Two things make this harder than "search and take one". First, the backends are unequal: the
    /// official API pages by opaque cursor and cannot take a page NUMBER, while osu.direct takes a
    /// keyword and nothing else (see <see cref="SearchFilters"/>). A random strategy expressed in
    /// filters only some backends understand silently collapses to "the first page of the default
    /// sort" on the rest — or, on a backend that needs a keyword to return anything useful, to
    /// nothing at all. So the randomness here is carried by a KEYWORD plus a sort, the two things
    /// every backend can express, with the page number kept only as extra spread for the backends
    /// that do page numerically.
    /// </para>
    ///
    /// <para>
    /// Second, search and download fail independently. Both are worth trying separately: at the time
    /// of writing NeriNyan answers 530 to a search while serving downloads perfectly well, so a
    /// radio that gave up on the first search failure would report "nothing available" on a machine
    /// that could still play music.
    /// </para>
    /// </summary>
    public class RadioService
    {
        private const int max_attempts = 3;

        private static readonly string[] sorts =
        {
            "ranked_desc", "ranked_asc", "favourites_desc", "plays_desc", "updated_desc"
        };

        /// <summary>
        /// The keyword pool the random pick draws from. Single letters rather than words: they match
        /// enormous numbers of titles and artists (so the pick is broad), every backend supports a
        /// keyword, and it gives the official API — which has no numeric paging for us to randomise
        /// over — a genuinely different result set per attempt instead of the same top-of-ranked
        /// page every time.
        /// </summary>
        private static readonly string[] keywords =
            "a b c d e f g h i j k l m n o p q r s t u v w x y z".Split(' ');

        private readonly IBeatmapMirror mirror;
        private readonly Func<int, int, int> rng;

        private readonly OfficialBeatmapSearch? official;
        private readonly IBindable<SearchApi>? searchApi;
        private readonly BeatmapCache? cache;

        /// <param name="mirror">The mirror chain, used for search when the official API is not in
        /// play and as the fallback when it fails.</param>
        /// <param name="rng">Overridable randomness, so a test can pin the pick.</param>
        /// <param name="official">osu!'s own search. Preferred when the user selected it and it has
        /// credentials — searching and downloading are already separate concerns in this app, and
        /// the official API is reachable in conditions where every mirror SEARCH is not.</param>
        /// <param name="searchApi">The user's backend preference, shared with the listing.</param>
        /// <param name="cache">Last resort: when nothing is reachable, the sets already on disk are
        /// still playable, and playing one beats reporting an error the user can do nothing about.</param>
        public RadioService(IBeatmapMirror mirror, Func<int, int, int>? rng = null,
                            OfficialBeatmapSearch? official = null, IBindable<SearchApi>? searchApi = null,
                            BeatmapCache? cache = null)
        {
            this.mirror = mirror;
            this.rng = rng ?? Random.Shared.Next;
            this.official = official;
            this.searchApi = searchApi;
            this.cache = cache;
        }

        /// <summary>Whether osu!'s own search should answer this pick.</summary>
        private bool useOfficial => searchApi?.Value == SearchApi.Official && official?.HasCredentials == true;

        public async Task<RadioPick> PickRandomAsync(CancellationToken ct = default)
        {
            var failures = new List<string>();

            for (int attempt = 0; attempt < max_attempts; attempt++)
            {
                var request = new SearchRequest
                {
                    Query = keywords[rng(0, keywords.Length)],
                    Page = rng(0, 200),
                    PageSize = 50,
                    Status = "ranked",
                    Sort = sorts[rng(0, sorts.Length)]
                };

                try
                {
                    var results = await searchAsync(request, ct).ConfigureAwait(false);
                    var candidates = results.Where(s => !s.DownloadDisabled).ToList();

                    if (candidates.Count == 0)
                        continue;

                    return new RadioPick(candidates[rng(0, candidates.Count)]);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Being called off is not a failed attempt. Without this the blanket catch below
                    // swallowed it and burned the remaining retries on a search nobody is waiting for
                    // any more — so a caller that cancels (an advance round superseded mid-lookup) both
                    // waited out the retries and got a null "no tracks available" for its trouble.
                    throw;
                }
                catch (Exception e)
                {
                    failures.Add(e.GetBaseException().Message);
                    Logger.Log($"Radio search attempt {attempt + 1} failed: {e.GetBaseException().Message}", level: LogLevel.Debug);
                }
            }

            return fallBackToCache(failures);
        }

        private async Task<List<BeatmapSetInfo>> searchAsync(SearchRequest request, CancellationToken ct)
        {
            if (useOfficial)
            {
                try
                {
                    var result = await official!.SearchAsync(request, ct).ConfigureAwait(false);
                    return result.Sets;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    // Same contract the listing uses (see BeatmapSearchEngine): an official failure
                    // is always recoverable by asking the mirrors the same question, so it never
                    // dead-ends — it just costs one extra round trip.
                    Logger.Log($"Radio: official search failed ({e.GetBaseException().Message}) — falling back to the mirror.",
                        level: LogLevel.Debug);
                }
            }

            return await mirror.SearchAsync(request, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Nothing answered. Rather than reporting a dead end, play something already downloaded —
        /// the cache is the one beatmap source that cannot go offline, and a user with sets on disk
        /// would rather hear one of them than read a retry notice.
        /// </summary>
        private RadioPick fallBackToCache(IReadOnlyList<string> failures)
        {
            var cached = cache?.CachedSetIds();

            if (cached is { Count: > 0 })
            {
                int id = cached[rng(0, cached.Count)];

                // Title/artist are unknown here — the cache stores files, not metadata. Playback
                // fills the real metadata in from the beatmap itself once it loads; this id is
                // enough for the round to find and play it.
                return new RadioPick(new BeatmapSetInfo { Id = id }, FromCache: true);
            }

            // Distinguishes the two ways to arrive here, because the user's next move differs: a
            // network problem is worth waiting out, an empty result set is not.
            string reason = failures.Count > 0
                ? "Can't reach any beatmap source right now."
                : "The beatmap search came back empty.";

            return new RadioPick(null, reason);
        }
    }
}
