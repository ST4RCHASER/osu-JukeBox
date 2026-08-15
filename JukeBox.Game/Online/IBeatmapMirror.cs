#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace JukeBox.Game.Online;

/// <summary>
/// Byte-level progress of an in-flight mirror download: how much has been written to the
/// destination so far, and the total the mirror advertised via <c>Content-Length</c>.
/// <paramref name="totalBytes"/> is null when the response carried no length (a chunked transfer,
/// which some mirrors use) — consumers surface that as indeterminate progress rather than guessing
/// a denominator. Invoked on whatever thread is draining the response body, so handlers must be
/// thread-safe.
/// </summary>
public delegate void DownloadProgressCallback(long bytesRead, long? totalBytes);

public interface IBeatmapMirror
{
    string Name { get; }
    Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Whether this mirror's search can express every filter <paramref name="request"/> carries.
    /// The three mirrors are wildly unequal here — NeriNyan takes the full osu-web filter
    /// vocabulary, catboy.best takes ruleset/status/paging only, and osu.direct takes nothing but a
    /// keyword — and a mirror that cannot express a filter does not reject it, it silently returns
    /// unfiltered results. <see cref="MirrorChain"/> therefore asks first, and only falls back to a
    /// mirror that would drop filters once no capable one could answer (reporting it through
    /// <see cref="SearchRequest.OnFiltersDropped"/>), so the filter rows can never quietly lie.
    ///
    /// Defaulted to true: a mirror that accepts everything — and any test stub, which is asked
    /// nothing more than the query — needs no opinion here.
    /// </summary>
    bool CanApplyFilters(SearchRequest request) => true;

    /// <summary>
    /// Streams set <paramref name="setId"/>'s .osz into <paramref name="destination"/>, optionally
    /// reporting byte progress along the way. <paramref name="progress"/> trails
    /// <paramref name="ct"/> so that every existing positional call site (which passes the token
    /// as the fourth argument) keeps compiling unchanged.
    /// </summary>
    Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null);
}
