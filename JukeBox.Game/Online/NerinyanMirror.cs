using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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

            // Star-range filters only exist on the b64 transport (a base64 JSON body passed as a
            // query param — POST /search is broken, always 400), so route through it whenever a
            // range is set. Everything else keeps using the plain legacy query string, which is
            // trivially cacheable/debuggable and known-good.
            if (r.HasStarRange)
            {
                string json = JsonSerializer.Serialize(new
                {
                    query = r.Query,
                    m = r.Mode ?? "",
                    sort = r.Sort,
                    s = r.Status,
                    page,
                    pageSize = r.PageSize,
                    extra,
                    option = r.Option ?? "",
                    difficultyRating = new
                    {
                        min = r.MinStars ?? 0,
                        max = r.MaxStars ?? 10,
                    },
                });

                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
                return $"{API_BASE}/search?b64={Uri.EscapeDataString(b64)}";
            }

            var q = new List<string>
            {
                $"q={Uri.EscapeDataString(r.Query)}",
                $"s={r.Status}", $"sort={r.Sort}", $"p={page}", $"ps={r.PageSize}"
            };
            if (extra.Length > 0) q.Add($"e={extra}");
            if (r.Mode != null) q.Add($"m={r.Mode}");
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
