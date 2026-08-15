#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Bindables;

namespace JukeBox.Game.Online
{
    /// <summary>
    /// Wraps the three concrete beatmap mirrors and reorders them per-call based on the user's
    /// <see cref="MirrorSource"/> preference: the preferred mirror is tried first, the rest follow
    /// in the default order (NeriNyan, catboy.best, osu.direct) — never excluded, so resilience is
    /// unaffected by the choice. Builds a fresh <see cref="MirrorChain"/> per call (cheap) so a live
    /// change to the preferred-mirror bindable takes effect on the very next request, and so the
    /// chain's own try-each/stream-reset fallback semantics are reused as-is.
    /// </summary>
    public class SwitchableMirror : IBeatmapMirror
    {
        private readonly IBeatmapMirror nerinyan;
        private readonly IBeatmapMirror catboy;
        private readonly IBeatmapMirror osuDirect;
        private readonly Bindable<MirrorSource> preferredMirror;

        public string Name => "switchable";

        public SwitchableMirror(IBeatmapMirror nerinyan, IBeatmapMirror catboy, IBeatmapMirror osuDirect, Bindable<MirrorSource> preferredMirror)
        {
            this.nerinyan = nerinyan;
            this.catboy = catboy;
            this.osuDirect = osuDirect;
            this.preferredMirror = preferredMirror;
        }

        public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
            => buildChain().SearchAsync(request, ct);

        public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
            => buildChain().DownloadAsync(setId, noVideo, destination, ct, progress);

        private MirrorChain buildChain()
        {
            var ordered = new List<IBeatmapMirror> { nerinyan, catboy, osuDirect };

            IBeatmapMirror? preferred = preferredMirror.Value switch
            {
                MirrorSource.Nerinyan => nerinyan,
                MirrorSource.Catboy => catboy,
                MirrorSource.OsuDirect => osuDirect,
                _ => null,
            };

            if (preferred != null)
            {
                ordered.Remove(preferred);
                ordered.Insert(0, preferred);
            }

            return new MirrorChain(ordered.ToArray());
        }
    }
}
