#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JukeBox.Game.Online
{
    public class OsuDirectMirror : IBeatmapMirror
    {
        public const string API_BASE = "https://osu.direct/api/v2";
        public const string DL_BASE = "https://osu.direct/api/d";
        private readonly HttpClient http;
        public string Name => "osu.direct";
        public OsuDirectMirror(HttpClient http) => this.http = http;

        internal static string BuildSearchUrl(SearchRequest r)
            => $"{API_BASE}/search?query={Uri.EscapeDataString(r.Query)}&limit={r.PageSize}";

        /// <summary>
        /// This API takes a keyword and a limit and nothing else — probing it live with mode,
        /// status, sort and paging parameters returns the byte-identical first page every time. So
        /// it can only honestly answer a bare keyword search on the first page; anything carrying a
        /// filter is left to a mirror that can express it (see
        /// <see cref="IBeatmapMirror.CanApplyFilters"/>).
        /// </summary>
        public bool CanApplyFilters(SearchRequest r)
            => r.Mode == null
               && r.Extra == SearchExtra.None
               && !r.HasStarRange
               && r.Sort == SearchRequest.DEFAULT_SORT
               && r.Status == SearchRequest.ANY_STATUS
               && r.Page == 0;

        public async Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
        {
            if (request.Option == SearchRequest.CHECKSUM_OPTION)
                return await searchByChecksumAsync(request.Query, ct).ConfigureAwait(false);

            string json = await http.GetStringAsync(BuildSearchUrl(request), ct).ConfigureAwait(false);
            return parseSets(json);
        }

        /// <summary>
        /// Resolves an .osu file's MD5 to the beatmapset containing it — what a dropped replay
        /// needs, since a replay identifies its beatmap by checksum and nothing else.
        ///
        /// <para>
        /// Two hops, because <c>/md5/{hash}</c> answers with the single DIFFICULTY that matches and
        /// the app deals in sets: the difficulty's <c>beatmapset_id</c> is then fetched through
        /// <c>/s/{id}</c>, which returns the same set schema the search endpoint does. An unknown
        /// hash (404) is an answer, not a failure, so it comes back as no results rather than an
        /// exception — a throw would read to <see cref="MirrorChain"/> as "this mirror is broken".
        /// </para>
        /// </summary>
        private async Task<List<BeatmapSetInfo>> searchByChecksumAsync(string md5, CancellationToken ct)
        {
            using var beatmapResponse = await http.GetAsync($"{API_BASE}/md5/{Uri.EscapeDataString(md5)}", ct).ConfigureAwait(false);

            if (beatmapResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new List<BeatmapSetInfo>();

            beatmapResponse.EnsureSuccessStatusCode();

            string beatmapJson = await beatmapResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var beatmapDoc = JsonDocument.Parse(beatmapJson);

            if (!beatmapDoc.RootElement.TryGetProperty("beatmapset_id", out var setIdElement)
                || !setIdElement.TryGetInt32(out int setId))
            {
                return new List<BeatmapSetInfo>();
            }

            using var setResponse = await http.GetAsync($"{API_BASE}/s/{setId}", ct).ConfigureAwait(false);

            if (setResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new List<BeatmapSetInfo>();

            setResponse.EnsureSuccessStatusCode();

            // A single set object, wrapped so the shared list parser can read it.
            string setJson = await setResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return BeatmapSetInfo.ParseList($"[{setJson}]");
        }

        public async Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
        {
            string url = $"{DL_BASE}/{setId}";
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await MirrorDownload.CopyAsync(response, destination, progress, ct).ConfigureAwait(false);
        }

        private static List<BeatmapSetInfo> parseSets(string json)
        {
            using var doc = JsonDocument.Parse(json);
            string arrayJson = doc.RootElement.ValueKind == JsonValueKind.Array
                ? json
                : doc.RootElement.GetProperty("beatmapsets").GetRawText();
            return BeatmapSetInfo.ParseList(arrayJson);
        }
    }
}
