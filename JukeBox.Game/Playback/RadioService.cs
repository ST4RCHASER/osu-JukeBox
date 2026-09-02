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
    /// <param name="CacheFilterRelaxed">Whether that cached pick had to ignore the user's mode
    /// filter because nothing on disk matched it. Distinct from <paramref name="FromCache"/>: the
    /// results are not merely older than asked for, they are the wrong MODE, which without saying
    /// so reads as the filter having no effect.</param>
    public record RadioPick(BeatmapSetInfo? Set, string? Failure = null, bool FromCache = false, bool CacheFilterRelaxed = false);

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

        /// <summary>
        /// How many cached sets the fallback will open to find one matching the mode filter before
        /// giving up and playing an unfiltered one. Bounded because the check is a disk read per
        /// set and this path only runs when the network is already failing — spending seconds
        /// stat-ing a full cache to honour a filter would turn a degraded radio into a hung one.
        /// </summary>
        private const int max_cache_probes = 40;

        private readonly IBeatmapMirror mirror;
        private readonly Func<int, int, int> rng;

        private readonly OfficialBeatmapSearch? official;
        private readonly IBindable<SearchApi>? searchApi;
        private readonly BeatmapCache? cache;
        private readonly RadioFilters filters;

        /// <param name="mirror">The mirror chain, used for search when the official API is not in
        /// play and as the fallback when it fails.</param>
        /// <param name="rng">Overridable randomness, so a test can pin the pick.</param>
        /// <param name="official">osu!'s own search. Preferred when the user selected it and it has
        /// credentials — searching and downloading are already separate concerns in this app, and
        /// the official API is reachable in conditions where every mirror SEARCH is not.</param>
        /// <param name="searchApi">The user's backend preference, shared with the listing.</param>
        /// <param name="cache">Last resort: when nothing is reachable, the sets already on disk are
        /// still playable, and playing one beats reporting an error the user can do nothing about.</param>
        /// <param name="filters">The user's station conditions (see <see cref="RadioFilters"/>).
        /// Defaulted to a free-standing neutral set, which asks exactly what the radio asked before
        /// there were filters.</param>
        public RadioService(IBeatmapMirror mirror, Func<int, int, int>? rng = null,
                            OfficialBeatmapSearch? official = null, IBindable<SearchApi>? searchApi = null,
                            BeatmapCache? cache = null, RadioFilters? filters = null)
        {
            this.mirror = mirror;
            this.rng = rng ?? Random.Shared.Next;
            this.official = official;
            this.searchApi = searchApi;
            this.cache = cache;
            this.filters = filters ?? new RadioFilters();
        }

        /// <summary>Whether osu!'s own search should answer this pick.</summary>
        private bool useOfficial => searchApi?.Value == SearchApi.Official && official?.HasCredentials == true;

        /// <summary>
        /// Which of the user's filters the backend about to answer can actually express. The same
        /// question the listing asks of the same two backends, answered by the same code
        /// (<see cref="SearchCapability"/>) — so a row the listing hides is a filter the radio
        /// doesn't send, and both move together as mirror health does.
        /// </summary>
        internal SearchFilters AvailableFilters
            => SearchCapability.For(searchApi?.Value ?? SearchApi.Mirror, mirror);

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
                    Sort = sorts[rng(0, sorts.Length)]
                };

                // Re-read per attempt rather than once per call: the mirror chain's capability
                // moves with mirror health, and a failed attempt is exactly the event that moves
                // it — so the retry asks what the backend can serve NOW.
                filters.Apply(request, AvailableFilters);

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
        ///
        /// <para>
        /// The user's filters are honoured as far as the disk can answer them, which is the mode
        /// and nothing else (see <see cref="RadioFilters.CanNarrowCache"/>). Deliberately a
        /// PREFERENCE rather than a requirement: a cache with no mania sets in it must still play
        /// something for a station set to mania, because the alternative is silence during an
        /// outage — filtering to zero here would trade the whole point of this fallback for a
        /// filter the user can loosen once they can see anything at all. The relaxation is reported
        /// rather than hidden.
        /// </para>
        /// </summary>
        private RadioPick fallBackToCache(IReadOnlyList<string> failures)
        {
            var cached = cache?.CachedSetIds();

            if (cached is { Count: > 0 })
            {
                bool relaxed = false;
                int id = pickCachedId(cached, ref relaxed);

                // Title/artist are unknown here — the cache stores files, not metadata. Playback
                // fills the real metadata in from the beatmap itself once it loads; this id is
                // enough for the round to find and play it.
                return new RadioPick(new BeatmapSetInfo { Id = id }, FromCache: true, CacheFilterRelaxed: relaxed);
            }

            // Distinguishes the two ways to arrive here, because the user's next move differs: a
            // network problem is worth waiting out, an empty result set is not.
            string reason = failures.Count > 0
                ? "Can't reach any beatmap source right now."
                : "The beatmap search came back empty.";

            return new RadioPick(null, reason);
        }

        /// <summary>
        /// Chooses one of <paramref name="cached"/>, preferring one that matches the mode filter.
        ///
        /// <para>
        /// Implemented as random PROBES rather than "filter the list, then pick" on purpose: each
        /// check opens the set's <c>.osu</c> headers off disk, so filtering first would read every
        /// cached set (hundreds, on a full cache) to make one choice, every time the network
        /// hiccups. Probing random candidates finds a match in a handful of reads whenever matches
        /// are common, and gives up after <see cref="max_cache_probes"/> when they are not — at
        /// which point an unfiltered pick is the honest answer anyway.
        /// </para>
        /// </summary>
        /// <param name="cached">The set ids currently on disk.</param>
        /// <param name="relaxed">Set to true when no probe matched and the returned id therefore
        /// ignores the mode filter.</param>
        private int pickCachedId(IReadOnlyList<int> cached, ref bool relaxed)
        {
            int fallback = cached[rng(0, cached.Count)];

            if (!filters.CanNarrowCache || cache == null)
                return fallback;

            for (int probe = 0; probe < max_cache_probes; probe++)
            {
                int id = cached[rng(0, cached.Count)];

                try
                {
                    if (filters.MatchesCachedSet(cache.LoadCached(id)))
                        return id;
                }
                catch (Exception e)
                {
                    // A cached set that won't scan (a half-extracted folder, a file the OS won't
                    // hand over) is not worth failing the whole fallback for — it just isn't a
                    // candidate. The unfiltered pick below still covers us.
                    Logger.Log($"Radio: couldn't inspect cached set {id} for the mode filter ({e.GetBaseException().Message})",
                        level: LogLevel.Debug);
                }
            }

            relaxed = true;
            return fallback;
        }
    }
}
