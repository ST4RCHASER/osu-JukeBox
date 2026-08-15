#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Logging;

namespace JukeBox.Game.Online
{
    public class MirrorChain : IBeatmapMirror
    {
        /// <summary>
        /// Per-mirror ceiling on a SEARCH. The mirrors are behind CDNs that occasionally accept a
        /// connection and then sit on it, and a search is on the keystroke path — waiting out
        /// <see cref="System.Net.Http.HttpClient"/>'s 100-second default before trying the next
        /// mirror would read as the app hanging. Downloads deliberately keep the long default:
        /// an .osz legitimately takes minutes.
        /// </summary>
        public static readonly TimeSpan SEARCH_TIMEOUT = TimeSpan.FromSeconds(15);

        private readonly IBeatmapMirror[] mirrors;
        private readonly MirrorHealth? health;

        /// <summary>Test-only override of <see cref="SEARCH_TIMEOUT"/>, so the stall path can be
        /// exercised without a test that really waits it out.</summary>
        internal TimeSpan SearchTimeout { get; set; } = SEARCH_TIMEOUT;

        public string Name => "chain";

        /// <summary>
        /// The union across every mirror that is currently REACHABLE, because the chain routes each
        /// request to whichever mirror can serve it: if NeriNyan is healthy, star ranges work even
        /// when osu.direct is the user's preferred mirror, since a starred request skips straight
        /// past it. Falls back to the union across all mirrors when every one of them is cooling
        /// down — they are still tried in that state, so the rows should still be offered.
        /// </summary>
        public SearchFilters SupportedFilters
        {
            get
            {
                var healthy = mirrors.Where(m => health?.IsCoolingDown(m) != true).ToList();

                if (healthy.Count == 0)
                    healthy = mirrors.ToList();

                return healthy.Aggregate(SearchFilters.None, (acc, m) => acc | m.SupportedFilters);
            }
        }

        public MirrorChain(params IBeatmapMirror[] mirrors)
            : this(null, mirrors)
        {
        }

        /// <param name="health">Shared across chains so a failure observed by one call is still
        /// remembered by the next (chains themselves are built per call) — null disables the
        /// memory entirely, which is what the plain constructor above gives tests.</param>
        /// <param name="mirrors">The backends to try, in preference order. That order is only the
        /// starting point: each request is served in tiers by what a mirror can actually express and
        /// whether it is in a failure cooldown (see SearchAsync), so a mirror listed first is not
        /// necessarily the one asked first.</param>
        public MirrorChain(MirrorHealth? health, params IBeatmapMirror[] mirrors)
        {
            this.health = health;
            this.mirrors = mirrors;
        }

        /// <summary>
        /// Tries mirrors in four tiers: mirrors that can express every filter on the request and are
        /// not in a failure cooldown, then healthy-but-limited ones, then the cooling-down mirrors as
        /// a last resort in the same order.
        ///
        /// <para>
        /// Capability comes first because a mirror that cannot express a filter does not reject it —
        /// it silently returns unfiltered results (see <see cref="IBeatmapMirror.CanApplyFilters"/>),
        /// and when a limited mirror is what ends up answering,
        /// <see cref="SearchRequest.OnFiltersDropped"/> fires so the listing can say so rather than
        /// showing a filter block that quietly does nothing.
        /// </para>
        ///
        /// <para>
        /// Health comes second so a mirror that is currently down (or, on macOS, permanently
        /// unreachable — see <see cref="MirrorHealth"/>) is not re-probed on every keystroke, while
        /// still being retried automatically once its cooldown lapses. Cooling-down mirrors are
        /// never dropped outright: if they are all we have, they are still tried.
        /// </para>
        /// </summary>
        public async Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest r, CancellationToken ct = default)
        {
            var capable = mirrors.Where(m => m.CanApplyFilters(r)).ToList();
            var limited = mirrors.Where(m => !m.CanApplyFilters(r)).ToList();

            bool healthy(IBeatmapMirror m) => health?.IsCoolingDown(m) != true;

            var order = capable.Where(healthy)
                               .Concat(limited.Where(healthy))
                               .Concat(capable.Where(m => !healthy(m)))
                               .Concat(limited.Where(m => !healthy(m)))
                               .ToList();

            var errors = new List<Exception>();

            foreach (var m in order)
            {
                try
                {
                    var results = await searchWithTimeoutAsync(m, r, ct).ConfigureAwait(false);

                    health?.RecordSuccess(m);

                    if (!m.CanApplyFilters(r))
                        r.OnFiltersDropped?.Invoke(m.Name);

                    return results;
                }
                catch (Exception e)
                {
                    // The CALLER cancelling is not a mirror failure — a superseded search must not
                    // condemn a perfectly healthy mirror, nor fall through to the next one.
                    if (ct.IsCancellationRequested)
                        throw;

                    health?.RecordFailure(m);
                    errors.Add(e);

                    Logger.Log($"Mirror search via {m.Name} failed ({e.GetBaseException().Message}); trying the next mirror.",
                        level: LogLevel.Debug);
                }
            }

            throw new AggregateException("all mirrors failed", errors);
        }

        private async Task<List<BeatmapSetInfo>> searchWithTimeoutAsync(IBeatmapMirror mirror, SearchRequest r, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SearchTimeout);

            return await mirror.SearchAsync(r, timeout.Token).ConfigureAwait(false);
        }

        public async Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
        {
            bool healthy(IBeatmapMirror m) => health?.IsCoolingDown(m) != true;

            // Same last-resort ordering as search, minus the capability tiers: every mirror can
            // serve any set id, so reachability is the only thing worth ordering on here.
            var order = mirrors.Where(healthy).Concat(mirrors.Where(m => !healthy(m))).ToList();

            var errors = new List<Exception>();

            foreach (var m in order)
            {
                // A prior mirror may have partially written to `destination` before failing;
                // rewind so the next attempt starts from a clean stream, not a corrupt tail.
                if (destination.CanSeek)
                {
                    destination.Position = 0;
                    destination.SetLength(0);
                }

                try
                {
                    // Progress is forwarded as-is, so a fallback attempt simply re-reports from
                    // zero against its own mirror's Content-Length — matching the rewind above.
                    await m.DownloadAsync(setId, noVideo, destination, ct, progress).ConfigureAwait(false);
                    health?.RecordSuccess(m);
                    return;
                }
                catch (Exception e)
                {
                    if (ct.IsCancellationRequested)
                        throw;

                    health?.RecordFailure(m);
                    errors.Add(e);
                }
            }

            throw new AggregateException("all mirrors failed", errors);
        }
    }
}
