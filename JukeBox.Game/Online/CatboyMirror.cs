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
    public class CatboyMirror : IBeatmapMirror
    {
        public const string API_BASE = "https://catboy.best/api/v2";
        public const string DL_BASE = "https://catboy.best/d";
        private readonly HttpClient http;
        public string Name => "catboy.best";
        public CatboyMirror(HttpClient http) => this.http = http;

        internal static string BuildSearchUrl(SearchRequest r)
        {
            var q = new List<string>
            {
                $"query={Uri.EscapeDataString(r.Query)}",
                $"limit={r.PageSize}",
            };

            // Ruleset ids, same numbering as osu! itself, and with the same "set contains such a
            // difficulty" semantics (verified against the live API).
            if (OfficialBeatmapSearch.ModeInt(r.Mode) is int mode)
                q.Add($"mode={mode}");

            if (StatusInt(r.Status) is int status)
                q.Add($"status={status}");

            // Offset paging — the only paging this API has; it takes no page number.
            if (r.Page > 0)
                q.Add($"offset={r.Page * r.PageSize}");

            return $"{API_BASE}/search?{string.Join("&", q)}";
        }

        /// <summary>
        /// osu-web's "approved" integer for a status NAME, or null when this mirror can't express it
        /// — <see cref="SearchRequest.ANY_STATUS"/> means no filter at all, and "leaderboard" (ranked
        /// + approved + qualified + loved together) has no single value here. Verified live: -2
        /// graveyard, -1 wip, 0 pending, 1 ranked, 2 approved, 3 qualified, 4 loved.
        /// </summary>
        internal static int? StatusInt(string status) => status switch
        {
            "graveyard" => -2,
            "wip" => -1,
            "pending" => 0,
            "ranked" => 1,
            "approved" => 2,
            "qualified" => 3,
            "loved" => 4,
            _ => null,
        };

        /// <summary>
        /// Ruleset, status and paging travel (see <see cref="BuildSearchUrl"/>); sort order, the
        /// video/storyboard extras and star ranges have no parameter on this API and are silently
        /// ignored if sent, so a request carrying any of them is one this mirror would answer
        /// wrongly rather than partially. "Leaderboard" is likewise inexpressible.
        /// </summary>
        public bool CanApplyFilters(SearchRequest r)
            => r.Extra == SearchExtra.None
               && !r.HasStarRange
               && r.Sort == SearchRequest.DEFAULT_SORT
               && (r.Status == SearchRequest.ANY_STATUS || StatusInt(r.Status) != null);

        public async Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
        {
            // This mirror has no checksum route (its /md5/ 404s as "not found", and its text search
            // matches metadata, so an MD5 query returns a cheerful empty list). Refusing outright
            // is the difference that matters to MirrorChain: it moves on to a mirror that CAN
            // answer, instead of accepting "no results" as the final word on whether the beatmap
            // a dropped replay names exists at all.
            if (request.Option == SearchRequest.CHECKSUM_OPTION)
                throw new NotSupportedException($"{Name} cannot look a beatmap up by checksum");

            string json = await http.GetStringAsync(BuildSearchUrl(request), ct).ConfigureAwait(false);
            return parseSets(json);
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
