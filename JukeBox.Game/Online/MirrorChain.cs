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

        public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest r, CancellationToken ct = default)
            => tryEach(m => m.SearchAsync(r, ct));

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

        private async Task<T> tryEach<T>(Func<IBeatmapMirror, Task<T>> action)
        {
            var errors = new List<Exception>();
            foreach (var m in mirrors)
            {
                try { return await action(m).ConfigureAwait(false); }
                catch (Exception e) { errors.Add(e); }
            }
            throw new AggregateException("all mirrors failed", errors);
        }
    }
}
