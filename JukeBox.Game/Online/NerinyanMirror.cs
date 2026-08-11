using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace JukeBox.Game.Online
{
    public class NerinyanMirror : IBeatmapMirror
    {
        public const string API_BASE = "https://api.nerinyan.moe";
        public const string DL_BASE = "https://dl.nerinyan.moe";
        private readonly HttpClient http;
        public string Name => "NeriNyan";
        public NerinyanMirror(HttpClient http) => this.http = http;

        internal static string BuildSearchUrl(SearchRequest r)
        {
            int maxPage = Math.Max(0, 10000 / Math.Max(1, r.PageSize) - 1);
            int page = Math.Min(r.Page, maxPage);
            string extra = r.Extra switch
            {
                SearchExtra.Storyboard => "storyboard",
                SearchExtra.Video => "video",
                SearchExtra.VideoAndStoryboard => "video.storyboard",
                _ => ""
            };
            var q = new List<string>
            {
                $"q={Uri.EscapeDataString(r.Query)}",
                $"s={r.Status}", $"sort={r.Sort}", $"p={page}", $"ps={r.PageSize}"
            };
            if (extra.Length > 0) q.Add($"e={extra}");
            if (r.Option != null) q.Add($"option={r.Option}");
            return $"{API_BASE}/search?{string.Join("&", q)}";
        }

        public async Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
        {
            string json = await http.GetStringAsync(BuildSearchUrl(request), ct).ConfigureAwait(false);
            return BeatmapSetInfo.ParseList(json);
        }

        public async Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default)
        {
            string url = $"{DL_BASE}/v2/d/{setId}" + (noVideo ? "?nv=1" : "");
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await response.Content.CopyToAsync(destination, ct).ConfigureAwait(false);
        }
    }
}
