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

        public async Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
        {
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
