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
    /// Derived from <see cref="SupportedFilters"/>: a request is servable when everything it
    /// actually exercises is something this mirror can express.
    /// </summary>
    bool CanApplyFilters(SearchRequest request) => (request.RequiredFilters & ~SupportedFilters) == SearchFilters.None;

    /// <summary>
    /// Which filters this mirror's search can express, as individual flags — the listing shows
    /// exactly the rows the backend about to serve it can honour, rather than offering controls
    /// that are silently ignored.
    ///
    /// Defaulted to <see cref="SearchFilters.All"/> so a test stub (asked nothing beyond a query)
    /// needs no opinion; every real mirror states its own.
    /// </summary>
    SearchFilters SupportedFilters => SearchFilters.All;

    /// <summary>
    /// Streams set <paramref name="setId"/>'s .osz into <paramref name="destination"/>, optionally
    /// reporting byte progress along the way. <paramref name="progress"/> trails
    /// <paramref name="ct"/> so that every existing positional call site (which passes the token
    /// as the fourth argument) keeps compiling unchanged.
    /// </summary>
    Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null);
}
