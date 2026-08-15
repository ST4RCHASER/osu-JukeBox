#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace JukeBox.Game.Online
{
    public class MirrorChain : IBeatmapMirror
    {
        private readonly IBeatmapMirror[] mirrors;
        public string Name => "chain";
        public MirrorChain(params IBeatmapMirror[] mirrors) => this.mirrors = mirrors;

        /// <summary>
        /// Mirrors that can express every filter on the request are tried FIRST, in the caller's
        /// preferred order; only if none of them answers do the rest get a turn, and then
        /// <see cref="SearchRequest.OnFiltersDropped"/> fires so the listing can say the results
        /// are unfiltered. Without this ordering a preferred-but-limited mirror (osu.direct takes
        /// nothing but a keyword) answers first and every filter row silently does nothing — which
        /// is exactly how this looked in the wild.
        /// </summary>
        public async Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest r, CancellationToken ct = default)
        {
            var capable = new List<IBeatmapMirror>();
            var limited = new List<IBeatmapMirror>();

            foreach (var m in mirrors)
                (m.CanApplyFilters(r) ? capable : limited).Add(m);

            var errors = new List<Exception>();

            foreach (var m in capable)
            {
                try { return await m.SearchAsync(r, ct).ConfigureAwait(false); }
                catch (Exception e) { errors.Add(e); }
            }

            foreach (var m in limited)
            {
                try
                {
                    var results = await m.SearchAsync(r, ct).ConfigureAwait(false);
                    r.OnFiltersDropped?.Invoke(m.Name);
                    return results;
                }
                catch (Exception e) { errors.Add(e); }
            }

            throw new AggregateException("all mirrors failed", errors);
        }

        public async Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
        {
            var errors = new List<Exception>();
            foreach (var m in mirrors)
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
                    return;
                }
                catch (Exception e) { errors.Add(e); }
            }
            throw new AggregateException("all mirrors failed", errors);
        }
    }
}
