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

        public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default)
            => tryEach<object?>(async m => { await m.DownloadAsync(setId, noVideo, destination, ct).ConfigureAwait(false); return null; });

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
