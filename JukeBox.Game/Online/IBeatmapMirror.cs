using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace JukeBox.Game.Online;

public interface IBeatmapMirror
{
    string Name { get; }
    Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default);
    Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default);
}
