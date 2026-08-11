# osu!JukeBox Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A standalone osu!framework desktop jukebox that searches/downloads beatmaps from NeriNyan (with mirrors), plays them queue-by-queue with endless radio, and renders storyboards (via ReOsuStoryboardPlayer.Core) and background video.

**Architecture:** osu!framework template layout (Game/Desktop/Resources/Tests). Services (mirror client, cache, queue, radio, playback) are plain classes or Components cached via DI in `JukeBoxGameBase`. Audio `Track` is the master clock (`DecouplingFramedClock`) driving video + storyboard. Storyboard: Core's `StoryboardUpdater` evaluates per frame; a `StoryboardLayer` maps object state onto pooled framework `Sprite`s.

**Tech Stack:** .NET 10, `ppy.osu.Framework` ≥ 2026.807.0, `ReOsuStoryboardPlayer.Core` (git submodule + ProjectReference), System.Text.Json, NUnit (via framework test infra).

## Global Constraints

- All four projects target `net10.0` (framework package is net8.0 — consumable; bump template csprojs after scaffold).
- Every Drawable subclass MUST be `partial` (source generators). If build warns about non-partial drawables, fix immediately.
- Transforms only in/after `LoadComplete()`, never in `[BackgroundDependencyLoader]`.
- DI: use `CacheAs<T>()` when caching under an interface type (`Cache()` registers the runtime type only).
- Search: legacy `GET https://api.nerinyan.moe/search` only — never `/v2/search`. Clamp `p * ps < 10000` before every request.
- Download: `GET https://dl.nerinyan.moe/v2/d/{setId}` — never pass `ns`/`nb`; `nv=1` only when the no-video setting is on.
- Mirror order everywhere: NeriNyan → catboy.best → osu.direct.
- Storyboard textures: `TextureStore` with `useAtlas: false, scaleAdjust: 1`.
- Storyboard space: 640×480 (854 wide for widescreen), scaled by `DrawHeight / 480`.
- Commit after every task (conventional commits). Run `dotnet build` before every commit; `dotnet test JukeBox.Game.Tests` where the task has tests.
- macOS is the dev/verify platform.

---

### Task 1: Scaffold repo and projects

**Files:**
- Create: whole solution via `dotnet new`, then edit `JukeBox.Game/JukeBox.Game.csproj`, `JukeBox.Desktop/JukeBox.Desktop.csproj`, `JukeBox.Game.Tests/JukeBox.Game.Tests.csproj`, `JukeBox.Resources/JukeBox.Resources.csproj`
- Create: `.gitmodules` (submodule `ReOsuStoryboardPlayer`)
- Create: `.gitignore`

**Interfaces:**
- Consumes: nothing.
- Produces: building solution `JukeBox.sln`; namespace root `JukeBox.Game`; `ReOsuStoryboardPlayer.Core` referenced from `JukeBox.Game`.

- [ ] **Step 1: Scaffold from template**

```bash
cd /Users/starchaser/Work/osu-JukeBox
dotnet new install ppy.osu.Framework.Templates
dotnet new osu-framework-game -n JukeBox
# template drops into ./JukeBox — flatten into repo root:
rsync -a JukeBox/ ./ && rm -rf JukeBox
```

Note: template names projects `JukeBox.Game`, `JukeBox.Desktop`, `JukeBox.Game.Tests`, `JukeBox.Resources` from `-n JukeBox`. If the generated game class is `JukeBoxGame`/`JukeBoxGameBase`, keep those names.

- [ ] **Step 2: Bump every csproj to net10.0**

In each of the 4 csprojs replace `<TargetFramework>net8.0</TargetFramework>` with `<TargetFramework>net10.0</TargetFramework>`. Delete `global.json` if it pins SDK 8 (`rm -f global.json`).

- [ ] **Step 3: Add Core submodule + reference**

```bash
git submodule add https://github.com/MikiraSora/ReOsuStoryboardPlayer external/ReOsuStoryboardPlayer
```

If the user's local fork is ahead of GitHub, instead: `git submodule add /Users/starchaser/Work/ReOsuStoryboardPlayer external/ReOsuStoryboardPlayer` (verify with the user only if the GitHub clone fails to build).

Add to `JukeBox.Game/JukeBox.Game.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="..\external\ReOsuStoryboardPlayer\ReOsuStoryboardPlayer.Core\ReOsuStoryboardPlayer.Core.csproj" />
</ItemGroup>
```

Core's `GeneratePackageOnBuild=true` may fail packing on build; if so add `<GeneratePackageOnBuild>false</GeneratePackageOnBuild>` via a `Directory.Build.props` in `external/` — do NOT edit the submodule.

- [ ] **Step 4: Build and run**

```bash
dotnet build
dotnet run --project JukeBox.Desktop &
sleep 10 && kill %1
```

Expected: build succeeds, window opens with template spinning box.

- [ ] **Step 5: Run template tests headless**

Run: `dotnet test JukeBox.Game.Tests`
Expected: template's `TestSceneSpinningBox` passes.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "chore: scaffold osu-framework template, net10, Core submodule"
```

---

### Task 2: Search models + JSON parsing

**Files:**
- Create: `JukeBox.Game/Online/BeatmapSetInfo.cs`
- Create: `JukeBox.Game.Tests/Fixtures/nerinyan_search.json` (copy 2–3 real sets from `curl 'https://api.nerinyan.moe/search?q=camellia&ps=3'`)
- Test: `JukeBox.Game.Tests/Online/BeatmapSetInfoParsingTest.cs`

**Interfaces:**
- Produces:
```csharp
namespace JukeBox.Game.Online;
public class BeatmapSetInfo
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string? TitleUnicode { get; set; }
    public string Artist { get; set; } = "";
    public string? ArtistUnicode { get; set; }
    public string Creator { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Video { get; set; }
    public bool Storyboard { get; set; }
    public AvailabilityInfo? Availability { get; set; }
    public List<BeatmapInfo> Beatmaps { get; set; } = new();
    public string DisplayTitle => string.IsNullOrEmpty(TitleUnicode) ? Title : TitleUnicode!;
    public string DisplayArtist => string.IsNullOrEmpty(ArtistUnicode) ? Artist : ArtistUnicode!;
    public bool DownloadDisabled => Availability?.DownloadDisabled == true;
    public static List<BeatmapSetInfo> ParseList(string json);   // System.Text.Json, snake_case
}
public class AvailabilityInfo { public bool DownloadDisabled { get; set; } }
public class BeatmapInfo
{
    public int Id { get; set; }
    public string Mode { get; set; } = "osu";   // osu|taiko|fruits|mania
    public string Version { get; set; } = "";
    public double DifficultyRating { get; set; }
    public int TotalLength { get; set; }
}
```

- [ ] **Step 1: Write failing test**

```csharp
// JukeBox.Game.Tests/Online/BeatmapSetInfoParsingTest.cs
using System.IO;
using JukeBox.Game.Online;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Online
{
    [TestFixture]
    public class BeatmapSetInfoParsingTest
    {
        [Test]
        public void ParsesNerinyanSearchArray()
        {
            string json = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "nerinyan_search.json"));
            var sets = BeatmapSetInfo.ParseList(json);
            Assert.That(sets, Is.Not.Empty);
            Assert.That(sets[0].Id, Is.GreaterThan(0));
            Assert.That(sets[0].Title, Is.Not.Empty);
            Assert.That(sets[0].Beatmaps, Is.Not.Empty);
            Assert.That(sets[0].Beatmaps[0].Version, Is.Not.Empty);
        }
    }
}
```

Add fixture as content in Tests csproj:
```xml
<ItemGroup><None Include="Fixtures\**" CopyToOutputDirectory="PreserveNewest" /></ItemGroup>
```

- [ ] **Step 2: Run — expect FAIL** (`BeatmapSetInfo` not defined). `dotnet test JukeBox.Game.Tests --filter BeatmapSetInfoParsingTest`

- [ ] **Step 3: Implement**

```csharp
// JukeBox.Game/Online/BeatmapSetInfo.cs  — properties as in Interfaces block, plus:
using System.Collections.Generic;
using System.Text.Json;

public static List<BeatmapSetInfo> ParseList(string json)
{
    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };
    return JsonSerializer.Deserialize<List<BeatmapSetInfo>>(json, options) ?? new List<BeatmapSetInfo>();
}
```

- [ ] **Step 4: Run — expect PASS.**
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: beatmapset models + nerinyan JSON parsing"`

---

### Task 3: Mirror interface + NeriNyan client (search)

**Files:**
- Create: `JukeBox.Game/Online/IBeatmapMirror.cs`, `JukeBox.Game/Online/SearchRequest.cs`, `JukeBox.Game/Online/NerinyanMirror.cs`
- Test: `JukeBox.Game.Tests/Online/NerinyanMirrorTest.cs`

**Interfaces:**
- Consumes: `BeatmapSetInfo.ParseList` (Task 2).
- Produces:
```csharp
namespace JukeBox.Game.Online;
public enum SearchExtra { None, Storyboard, Video, VideoAndStoryboard }
public class SearchRequest
{
    public string Query = "";
    public int Page;                       // 0-indexed
    public int PageSize = 50;
    public string Status = "ranked";
    public string Sort = "ranked_desc";
    public SearchExtra Extra = SearchExtra.None;
}
public interface IBeatmapMirror
{
    string Name { get; }
    Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default);
    Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default);
}
// NerinyanMirror(HttpClient http) : IBeatmapMirror
// internal static string BuildSearchUrl(SearchRequest r)  — exposed for tests
```

- [ ] **Step 1: Failing tests (URL building + parsing via stub handler)**

```csharp
// JukeBox.Game.Tests/Online/NerinyanMirrorTest.cs
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Online
{
    public class StubHandler : HttpMessageHandler
    {
        public string ResponseBody = "[]";
        public string? LastUrl;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody)
            });
        }
    }

    [TestFixture]
    public class NerinyanMirrorTest
    {
        [Test]
        public void BuildsLegacySearchUrl()
        {
            var url = NerinyanMirror.BuildSearchUrl(new SearchRequest { Query = "camellia", Extra = SearchExtra.Storyboard, Page = 2, PageSize = 50 });
            Assert.That(url, Does.StartWith("https://api.nerinyan.moe/search?"));
            Assert.That(url, Does.Contain("q=camellia"));
            Assert.That(url, Does.Contain("e=storyboard"));
            Assert.That(url, Does.Contain("p=2"));
            Assert.That(url, Does.Not.Contain("/v2/"));
        }

        [Test]
        public void ClampsPageBelow10kWindow()
        {
            var url = NerinyanMirror.BuildSearchUrl(new SearchRequest { Page = 500, PageSize = 50 });
            Assert.That(url, Does.Contain("p=199"));  // 199*50 < 10000, 200*50 hits the cap
        }

        [Test]
        public async Task SearchParsesResponse()
        {
            var handler = new StubHandler { ResponseBody = "[{\"id\":1,\"title\":\"t\",\"artist\":\"a\",\"creator\":\"c\",\"beatmaps\":[]}]" };
            var mirror = new NerinyanMirror(new HttpClient(handler));
            var results = await mirror.SearchAsync(new SearchRequest());
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Id, Is.EqualTo(1));
        }
    }
}
```

- [ ] **Step 2: Run — expect FAIL** (types missing).

- [ ] **Step 3: Implement**

```csharp
// JukeBox.Game/Online/NerinyanMirror.cs
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
```

- [ ] **Step 4: Run — expect PASS.**
- [ ] **Step 5: Commit** `git commit -am "feat: nerinyan mirror client (search + download)"`

---

### Task 4: Fallback mirrors + MirrorChain

**Files:**
- Create: `JukeBox.Game/Online/CatboyMirror.cs`, `JukeBox.Game/Online/OsuDirectMirror.cs`, `JukeBox.Game/Online/MirrorChain.cs`
- Test: `JukeBox.Game.Tests/Online/MirrorChainTest.cs`

**Interfaces:**
- Consumes: `IBeatmapMirror`, `SearchRequest`, `BeatmapSetInfo` (Tasks 2–3).
- Produces: `MirrorChain(params IBeatmapMirror[] mirrors) : IBeatmapMirror` — tries mirrors in order on exception/non-success; `CatboyMirror(HttpClient)`, `OsuDirectMirror(HttpClient)`.

- [ ] **Step 1: Failing test**

```csharp
// JukeBox.Game.Tests/Online/MirrorChainTest.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Online
{
    public class FakeMirror : IBeatmapMirror
    {
        public string Name => "fake";
        public bool Fail;
        public int SearchCalls;
        public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest r, CancellationToken ct = default)
        {
            SearchCalls++;
            if (Fail) throw new IOException("down");
            return Task.FromResult(new List<BeatmapSetInfo> { new BeatmapSetInfo { Id = 42 } });
        }
        public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default)
        {
            if (Fail) throw new IOException("down");
            destination.WriteByte(1);
            return Task.CompletedTask;
        }
    }

    [TestFixture]
    public class MirrorChainTest
    {
        [Test]
        public async Task FallsBackToSecondMirror()
        {
            var a = new FakeMirror { Fail = true };
            var b = new FakeMirror();
            var chain = new MirrorChain(a, b);
            var results = await chain.SearchAsync(new SearchRequest());
            Assert.That(results[0].Id, Is.EqualTo(42));
            Assert.That(a.SearchCalls, Is.EqualTo(1));
        }

        [Test]
        public void ThrowsWhenAllFail()
        {
            var chain = new MirrorChain(new FakeMirror { Fail = true }, new FakeMirror { Fail = true });
            Assert.ThrowsAsync<AggregateException>(() => chain.SearchAsync(new SearchRequest()));
        }
    }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement**

```csharp
// JukeBox.Game/Online/MirrorChain.cs
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
```

```csharp
// CatboyMirror.cs — search: GET https://catboy.best/api/v2/search?query={q}&limit={ps}
// download: GET https://catboy.best/d/{setId}   (no nv support — ignore noVideo)
// OsuDirectMirror.cs — search: GET https://osu.direct/api/v2/search?query={q}&limit={ps}
// download: GET https://osu.direct/api/d/{setId}
// Both parse with BeatmapSetInfo.ParseList; wrap parse in try/catch and if the v2 response is
// an object with "beatmapsets" instead of a bare array, deserialize that property instead:
//   using var doc = JsonDocument.Parse(json);
//   string arrayJson = doc.RootElement.ValueKind == JsonValueKind.Array
//       ? json : doc.RootElement.GetProperty("beatmapsets").GetRawText();
```

Implement both mirrors fully with that shape-sniffing parse — same class structure as `NerinyanMirror`, ~40 lines each. `DownloadAsync` identical to NeriNyan's minus the `nv` query.

- [ ] **Step 4: Run — expect PASS.** Also verify live once (not in CI): `dotnet run` a scratch — skip; live verification happens in Task 12 wiring.
- [ ] **Step 5: Commit** `git commit -am "feat: catboy/osu.direct mirrors + ordered fallback chain"`

---

### Task 5: BeatmapCache (download, extract, scan, index)

**Files:**
- Create: `JukeBox.Game/Beatmaps/BeatmapCache.cs`, `JukeBox.Game/Beatmaps/CachedBeatmapSet.cs`, `JukeBox.Game/Beatmaps/OsuFileScanner.cs`
- Create: `JukeBox.Game.Tests/Fixtures/fixture.osz` (zip containing minimal `test.osu` + `audio.mp3` stub + `sb.osb`; build it in the test SetUp instead of committing a binary — see test)
- Test: `JukeBox.Game.Tests/Beatmaps/BeatmapCacheTest.cs`

**Interfaces:**
- Consumes: `IBeatmapMirror.DownloadAsync` (Task 3).
- Produces:
```csharp
namespace JukeBox.Game.Beatmaps;
public class CachedBeatmapSet
{
    public int SetId;
    public string Directory = "";          // absolute path of extracted folder
    public string? AudioFile;              // absolute path, from [General] AudioFilename of first difficulty
    public string? OsbFile;                // absolute path or null
    public List<string> OsuFiles = new();  // absolute paths
    public string? VideoFile;              // from Video event, if file exists
    public string? BackgroundFile;         // from background event, if file exists
    public bool Widescreen;
    public string? PreferredOsuFile;       // first Mode:0 diff, else first diff
}
public class OsuFileScanner
{
    // Reads [General] (AudioFilename, Mode, WidescreenStoryboard) and [Events]
    // (background "0,0,\"bg.jpg\"" and video "Video,offset,\"v.mp4\"" / "1,offset,..." lines).
    public static OsuFileInfo Scan(string osuPath);
}
public class OsuFileInfo
{
    public string? AudioFilename; public int Mode; public bool Widescreen;
    public string? BackgroundFilename; public string? VideoFilename; public double VideoOffsetMs;
}
public class BeatmapCache
{
    public BeatmapCache(string rootDirectory, IBeatmapMirror mirror, bool noVideo = false);
    public bool IsCached(int setId);
    public Task<CachedBeatmapSet> GetAsync(int setId, CancellationToken ct = default); // dedupes in-flight
    public CachedBeatmapSet LoadFromDirectory(int setId, string dir);                  // scan-only (used by tests + after extract)
}
```

- [ ] **Step 1: Failing tests**

```csharp
// JukeBox.Game.Tests/Beatmaps/BeatmapCacheTest.cs
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Online;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Beatmaps
{
    [TestFixture]
    public class BeatmapCacheTest
    {
        private string tmp = null!;

        private const string osu_content = "osu file format v14\n\n[General]\nAudioFilename: audio.mp3\nMode: 0\nWidescreenStoryboard: 1\n\n[Events]\n//Background and Video events\n0,0,\"bg.jpg\",0,0\nVideo,100,\"movie.mp4\"\n";

        [SetUp]
        public void SetUp() => tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        [TearDown]
        public void TearDown() { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }

        private string makeOsz()
        {
            string dir = Path.Combine(tmp, "build"); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "test.osu"), osu_content);
            File.WriteAllText(Path.Combine(dir, "sb.osb"), "[Events]\n");
            File.WriteAllBytes(Path.Combine(dir, "audio.mp3"), new byte[] { 0xFF });
            File.WriteAllBytes(Path.Combine(dir, "bg.jpg"), new byte[] { 0xFF });
            string osz = Path.Combine(tmp, "fixture.osz");
            ZipFile.CreateFromDirectory(dir, osz);
            return osz;
        }

        [Test]
        public void ScannerReadsGeneralAndEvents()
        {
            Directory.CreateDirectory(tmp);
            string osu = Path.Combine(tmp, "a.osu");
            File.WriteAllText(osu, osu_content);
            var info = OsuFileScanner.Scan(osu);
            Assert.That(info.AudioFilename, Is.EqualTo("audio.mp3"));
            Assert.That(info.Mode, Is.EqualTo(0));
            Assert.That(info.Widescreen, Is.True);
            Assert.That(info.BackgroundFilename, Is.EqualTo("bg.jpg"));
            Assert.That(info.VideoFilename, Is.EqualTo("movie.mp4"));
        }

        [Test]
        public async Task DownloadsExtractsAndScans()
        {
            string osz = makeOsz();
            var mirror = new FileMirror(osz);   // serves the osz bytes as DownloadAsync
            var cache = new BeatmapCache(Path.Combine(tmp, "cache"), mirror);
            var set = await cache.GetAsync(123);
            Assert.That(cache.IsCached(123), Is.True);
            Assert.That(File.Exists(set.AudioFile), Is.True);
            Assert.That(set.OsbFile, Does.EndWith("sb.osb"));
            Assert.That(set.PreferredOsuFile, Does.EndWith("test.osu"));
            Assert.That(set.Widescreen, Is.True);
            Assert.That(set.VideoFile, Is.Null); // movie.mp4 not present in zip → null
        }
    }

    public class FileMirror : IBeatmapMirror
    {
        private readonly string path;
        public FileMirror(string path) => this.path = path;
        public string Name => "file";
        public System.Threading.Tasks.Task<System.Collections.Generic.List<BeatmapSetInfo>> SearchAsync(SearchRequest r, System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult(new System.Collections.Generic.List<BeatmapSetInfo>());
        public async System.Threading.Tasks.Task DownloadAsync(int setId, bool noVideo, Stream destination, System.Threading.CancellationToken ct = default)
        {
            using var fs = File.OpenRead(path);
            await fs.CopyToAsync(destination, ct);
        }
    }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement.** `OsuFileScanner`: line-read until second section; `[General]` `key: value` pairs; `[Events]` lines starting `0,` (background: third CSV field, strip quotes) or `Video,`/`1,` (video: offset second field, filename third). `BeatmapCache.GetAsync`:

```csharp
// core of GetAsync — dedupe + download-to-temp + extract:
private readonly ConcurrentDictionary<int, Task<CachedBeatmapSet>> inflight = new();

public Task<CachedBeatmapSet> GetAsync(int setId, CancellationToken ct = default)
    => inflight.GetOrAdd(setId, id => getInternal(id, ct));

private async Task<CachedBeatmapSet> getInternal(int setId, CancellationToken ct)
{
    try
    {
        string dir = Path.Combine(root, setId.ToString());
        if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.osu").Any())
            return LoadFromDirectory(setId, dir);

        string tmpOsz = Path.Combine(root, $"{setId}.osz.part");
        Directory.CreateDirectory(root);
        await using (var fs = File.Create(tmpOsz))
            await mirror.DownloadAsync(setId, noVideo, fs, ct).ConfigureAwait(false);
        string tmpDir = dir + ".extracting";
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        ZipFile.ExtractToDirectory(tmpOsz, tmpDir);
        File.Delete(tmpOsz);
        Directory.Move(tmpDir, dir);
        return LoadFromDirectory(setId, dir);
    }
    finally { inflight.TryRemove(setId, out _); }
}
```

`LoadFromDirectory` scans all `*.osu` (case-insensitive), picks `PreferredOsuFile` = first with `Mode == 0` else first; resolves `AudioFile`/`BackgroundFile`/`VideoFile` from the preferred diff's scan, null when missing on disk; `OsbFile` = first `*.osb`.

- [ ] **Step 4: Run — expect PASS.**
- [ ] **Step 5: Commit** `git commit -am "feat: beatmap cache (download, extract, scan)"`

---

### Task 6: MusicQueue + RadioService

**Files:**
- Create: `JukeBox.Game/Playback/MusicQueue.cs`, `JukeBox.Game/Playback/RadioService.cs`
- Test: `JukeBox.Game.Tests/Playback/MusicQueueTest.cs`, `JukeBox.Game.Tests/Playback/RadioServiceTest.cs`

**Interfaces:**
- Consumes: `IBeatmapMirror`, `SearchRequest`, `BeatmapSetInfo`.
- Produces:
```csharp
namespace JukeBox.Game.Playback;
public class MusicQueue
{
    public readonly BindableList<BeatmapSetInfo> Items = new();
    public void Enqueue(BeatmapSetInfo set);      // ignores duplicates already queued
    public BeatmapSetInfo? PopNext();             // removes and returns head, null if empty
}
public class RadioService
{
    public RadioService(IBeatmapMirror mirror, Func<int, int, int> rng = null); // rng(minInclusive,maxExclusive), default Random.Shared
    public Task<BeatmapSetInfo?> PickRandomAsync(CancellationToken ct = default);
    // random p in [0,199], ps=50, s=ranked, random sort among
    // {ranked_desc, ranked_asc, favourites_desc, plays_desc, updated_desc};
    // random element from page; skips DownloadDisabled; up to 3 attempts; null if all fail.
}
```

- [ ] **Step 1: Failing tests** — queue: enqueue/pop order, duplicate ignore; radio: with a `FakeMirror` returning one disabled + one enabled set assert the enabled one is picked; with always-throwing mirror assert null after 3 attempts (count SearchCalls).

```csharp
[Test]
public void EnqueuePopFifoAndDedupe()
{
    var q = new MusicQueue();
    var a = new BeatmapSetInfo { Id = 1 }; var b = new BeatmapSetInfo { Id = 2 };
    q.Enqueue(a); q.Enqueue(b); q.Enqueue(new BeatmapSetInfo { Id = 1 });
    Assert.That(q.Items, Has.Count.EqualTo(2));
    Assert.That(q.PopNext()!.Id, Is.EqualTo(1));
    Assert.That(q.PopNext()!.Id, Is.EqualTo(2));
    Assert.That(q.PopNext(), Is.Null);
}

[Test]
public async Task RadioSkipsDownloadDisabled()
{
    var mirror = new ListMirror(new List<BeatmapSetInfo>
    {
        new BeatmapSetInfo { Id = 1, Availability = new AvailabilityInfo { DownloadDisabled = true } },
        new BeatmapSetInfo { Id = 2 },
    });
    var radio = new RadioService(mirror, (min, max) => min);  // deterministic rng
    var pick = await radio.PickRandomAsync();
    Assert.That(pick!.Id, Is.EqualTo(2));
}
```

(`ListMirror` = FakeMirror variant whose `SearchAsync` returns the provided list.)

- [ ] **Step 2: Run — expect FAIL.**
- [ ] **Step 3: Implement** (`BindableList` from `osu.Framework.Bindables`; `Enqueue` no-ops when `Items.Any(i => i.Id == set.Id)`).
- [ ] **Step 4: Run — expect PASS.**
- [ ] **Step 5: Commit** `git commit -am "feat: music queue + radio random picker"`

---

### Task 7: PlaybackController (track = clock)

**Files:**
- Create: `JukeBox.Game/Playback/PlaybackController.cs`
- Test: `JukeBox.Game.Tests/Visual/TestScenePlaybackController.cs` (interactive; headless-safe asserts)

**Interfaces:**
- Consumes: `CachedBeatmapSet` (Task 5).
- Produces:
```csharp
namespace JukeBox.Game.Playback;
public partial class PlaybackController : Component
{
    public readonly Bindable<CachedBeatmapSet?> Current = new();
    public readonly BindableDouble Volume = new(1) { MinValue = 0, MaxValue = 1 };
    public IFrameBasedClock PlaybackClock { get; }        // DecouplingFramedClock over current track, framed
    public event Action? TrackCompleted;                   // fired on update thread
    public bool IsPlaying { get; }
    public double CurrentTimeMs { get; }
    public double LengthMs { get; }
    public Task PlayAsync(CachedBeatmapSet set);           // loads track from set.AudioFile, starts
    public void TogglePause();
    public void Stop();
    public void Seek(double ms);
}
```

Implementation notes (all inside the class):
- `[Resolved] AudioManager audio` — per-set track store: `audio.GetTrackStore(new StorageBackedResourceStore(new NativeStorage(set.Directory)))` then `Get(Path.GetFileName(set.AudioFile))`. (`NativeStorage` is `osu.Framework.Platform.NativeStorage`.)
- Keep one `DecouplingFramedClock decoupled = new() { AllowDecoupling = true }` + `FramedClock` wrapper created once; `decoupled.ChangeSource(track)` on each `PlayAsync` — consumers keep a stable `PlaybackClock` reference.
- `track.Completed += () => Schedule(() => TrackCompleted?.Invoke());`
- Bind `Volume` to `track.Volume` (`track.Volume.BindTo(Volume)` won't survive track swap — re-apply `AddAdjustment`/direct `track.Volume.Value = Volume.Value` binding per track load; simplest: `Volume.BindValueChanged(v => currentTrack?.Volume.Set(v.NewValue))` plus set on load).
- Dispose the previous track on swap.

- [ ] **Step 1: Write TestScene** (`JukeBox.Game.Tests/Visual/TestScenePlaybackController.cs`): `AddStep("play fixture", ...)` building a `CachedBeatmapSet` around a bundled short silence mp3 fixture (generate a 1-second silent wav in SetUp with a hand-written 44-byte RIFF header + zero samples — BASS plays wav), `AddUntilStep("clock advances", () => controller.CurrentTimeMs > 0)`, `AddStep("pause", ...)`, `AddAssert("not playing", () => !controller.IsPlaying)`.
- [ ] **Step 2: Run — expect FAIL** (class missing).
- [ ] **Step 3: Implement per notes above.**
- [ ] **Step 4: `dotnet test JukeBox.Game.Tests --filter TestScenePlaybackController` — expect PASS.**
- [ ] **Step 5: Commit** `git commit -am "feat: playback controller with track-driven clock"`

---

### Task 8: StoryboardLayer (Core → framework sprites)

**Files:**
- Create: `JukeBox.Game/Storyboard/StoryboardLayer.cs`, `JukeBox.Game/Storyboard/StoryboardLoader.cs`
- Test: `JukeBox.Game.Tests/Visual/TestSceneStoryboardLayer.cs` + fixture storyboard built in test SetUp

**Interfaces:**
- Consumes: `CachedBeatmapSet`; Core: `OsuFileReader`, `VariableReader`, `VariableCollection`, `EventReader`, `StoryboardReader`, `StoryboardObject`, `StoryboardUpdater`, `StoryboardOptimzerManager`, `RuntimeOptimzer`, `StoryboardBackgroundObject`.
- Produces:
```csharp
namespace JukeBox.Game.Storyboard;
public static class StoryboardLoader
{
    // Parses .osb + selected .osu with Core's chain, merges per-layer (.osu first then .osb),
    // assigns Z, CalculateAndApplyBaseFrameTime, optimizes level 2. Pure CPU, call off update thread.
    public static List<StoryboardObject> Load(string? osbFile, string? osuFile);
}
public partial class StoryboardLayer : CompositeDrawable
{
    public StoryboardLayer(CachedBeatmapSet set);   // parses in BDL via StoryboardLoader
    // Size = (854 or 640, 480); host container scales it.
    public int VisibleSpriteCount { get; }          // for tests
}
```

Implementation core (this is the heart of the app — full code in plan):

```csharp
// StoryboardLoader.Load — port of Example1 + StoryboardInstance merge:
public static List<StoryboardObject> Load(string? osbFile, string? osuFile)
{
    var osb = osbFile != null ? readFile(osbFile) : new List<StoryboardObject>();
    var osu = osuFile != null ? readFile(osuFile) : new List<StoryboardObject>();
    var result = new List<StoryboardObject>();
    foreach (Layer layer in Enum.GetValues<Layer>())
    {
        result.AddRange(osu.Where(o => o.layer == layer));
        result.AddRange(osb.Where(o => o.layer == layer).Select(o => { o.FromOsbFile = true; return o; }));
    }
    int z = 0;
    foreach (var obj in result) obj.Z = z++;
    StoryboardOptimzerManager.AddOptimzer<RuntimeOptimzer>();
    StoryboardOptimzerManager.Optimze(2, result);
    return result;
}

private static List<StoryboardObject> readFile(string path)
{
    var reader = new OsuFileReader(path);
    var vars = new VariableCollection(new VariableReader(reader).EnumValues());
    var events = new EventReader(reader, vars);
    var objs = new StoryboardReader(events).EnumValues().Where(o => o != null).ToList();
    foreach (var o in objs) o.CalculateAndApplyBaseFrameTime();
    return objs;
}
```

```csharp
// StoryboardLayer essentials:
private StoryboardUpdater updater = null!;
private TextureStore textures = null!;
private readonly Dictionary<StoryboardObject, Sprite> sprites = new();

[BackgroundDependencyLoader]
private void load(GameHost host)
{
    Size = new Vector2(set.Widescreen ? 854 : 640, 480);
    var objects = StoryboardLoader.Load(set.OsbFile, set.PreferredOsuFile);
    foreach (var bg in objects.OfType<StoryboardBackgroundObject>())
    {
        var tex = getTexture(bg.ImageFilePath);
        if (tex != null) bg.AdjustScale(tex.Height);
    }
    updater = new StoryboardUpdater(objects);
    textures = new TextureStore(host.Renderer,
        host.CreateTextureLoaderStore(new StorageBackedResourceStore(new NativeStorage(set.Directory))),
        useAtlas: false, scaleAdjust: 1);
}

private Texture? getTexture(string imagePath)
{
    // Core lowercases + backslashes paths; cache dir is the real folder → normalize:
    string p = imagePath.Replace('\\', '/');
    return textures.Get(p) ?? textures.Get(Path.ChangeExtension(p, "png")) ?? textures.Get(p + "-0");
}

protected override void Update()
{
    base.Update();
    updater.Update((float)Clock.CurrentTime);
    var visible = updater.UpdatingStoryboardObjects;
    // hide sprites for objects no longer active
    foreach (var (obj, sprite) in sprites)
        if (!obj.IsVisible || !visible.Contains(obj)) sprite.Alpha = 0;

    float depth = 0;
    foreach (var obj in visible)   // already Z-sorted
    {
        if (!obj.IsVisible) continue;
        if (!sprites.TryGetValue(obj, out var sprite))
        {
            sprite = new Sprite { Anchor = Anchor.TopLeft };
            sprites[obj] = sprite;
            AddInternal(sprite);
        }
        var tex = getTexture(obj.ImageFilePath);
        if (tex == null) { sprite.Alpha = 0; continue; }
        if (sprite.Texture != tex) { sprite.Texture = tex; sprite.Size = new Vector2(tex.Width, tex.Height); }
        // origin: Core OriginOffset is normalized offset from sprite center (−0.5..0.5)
        sprite.Origin = Anchor.Custom;
        sprite.OriginPosition = new Vector2((0.5f + obj.OriginOffset.x) * tex.Width, (0.5f + obj.OriginOffset.y) * tex.Height);
        // 640-space: Core Postion is in 640×480 with widescreen objects using x∈[-107,747]
        sprite.Position = new Vector2(obj.Postion.X + (set.Widescreen ? 107 : 0), obj.Postion.Y);
        sprite.Rotation = MathHelper.RadiansToDegrees(obj.Rotate);
        sprite.Scale = new Vector2(obj.Scale.X * (obj.IsHorizonFlip ? -1 : 1), obj.Scale.Y * (obj.IsVerticalFlip ? -1 : 1));
        sprite.Colour = new Colour4(obj.Color.X, obj.Color.Y, obj.Color.Z, 255);
        sprite.Alpha = obj.Color.W / 255f;
        sprite.Blending = obj.IsAdditive ? BlendingParameters.Additive : BlendingParameters.Inherit;
        ChangeInternalChildDepth(sprite, depth);   // maintain paint order
        depth -= 1;
    }
}
```

Field-name checks against Core (verify at implementation time, adjust if wrong): `Postion` (Vector: `.X/.Y` — Core `Vector` uses lowercase `x/y`? check `PrimitiveValue/Vector.cs` and use actual casing), `Rotate` is radians in Core (player computes cos/sin directly), `Color` is `ByteVec4` bytes, `OriginOffset` is `HalfVector`. `updater.UpdatingStoryboardObjects` is `List<StoryboardObject>`. `Contains` on list per sprite is O(n²) — acceptable v1; if slow, maintain a `HashSet` copied per frame.

- [ ] **Step 1: TestScene** — build a temp map dir in SetUp: `bg.png` (solid 4×4 png written from bytes), `.osb` with one sprite `Sprite,Background,Centre,"bg.png",320,240` + `_F,0,0,5000,0,1` + `_M,0,0,5000,320,240,320,240`; drive with `ManualClock`:

```csharp
private readonly ManualClock manual = new ManualClock();
// wrap: layer.Clock = new FramedClock(manual);
AddStep("t=2500", () => manual.CurrentTime = 2500);
AddUntilStep("one sprite visible", () => layer.VisibleSpriteCount == 1);
AddStep("t=6000", () => manual.CurrentTime = 6000);
AddUntilStep("no sprite visible", () => layer.VisibleSpriteCount == 0);
```

- [ ] **Step 2: Run — expect FAIL.**
- [ ] **Step 3: Implement per code above** (fix Core field casing to whatever compiles — check `Vector.x` lowercase).
- [ ] **Step 4: Run — expect PASS.**
- [ ] **Step 5: Commit** `git commit -am "feat: storyboard layer driven by ReOsuStoryboardPlayer.Core"`

---

### Task 9: NowPlayingScreen (background + video + storyboard on the shared clock)

**Files:**
- Create: `JukeBox.Game/Screens/NowPlayingScreen.cs`, `JukeBox.Game/Screens/BeatmapVisuals.cs`
- Test: `JukeBox.Game.Tests/Visual/TestSceneBeatmapVisuals.cs`

**Interfaces:**
- Consumes: `PlaybackController` (`.PlaybackClock`, `.Current`), `StoryboardLayer`, `CachedBeatmapSet`, framework `Video`.
- Produces:
```csharp
public partial class BeatmapVisuals : CompositeDrawable
{
    public BeatmapVisuals(CachedBeatmapSet set, IFrameBasedClock playbackClock);
    // internal stack: background Sprite (fill, dimmed 0.7) → Video (if set.VideoFile != null,
    //   offset by scanner's VideoOffsetMs) → StoryboardLayer, all with Clock = playbackClock.
    // Aspect-fit: 480-height space scaled to DrawHeight/480, centred.
}
public partial class NowPlayingScreen : Screen
{
    // Binds PlaybackController.Current; on change LoadComponentAsync(new BeatmapVisuals(...), swap).
}
```

Video: `new Video(set.VideoFile) { RelativeSizeAxes = Axes.Both, FillMode = FillMode.Fit }` inside a clock-offset container (`Container { Clock = new FramedOffsetClock(playbackClock) { Offset = -info.VideoOffsetMs } }` — verify sign at runtime: video must start at storyboard time = offset). Wrap creation in try/catch → on decode failure remove layer.

- [ ] **Step 1: TestScene** — fixture set with bg only; assert visuals load (`AddUntilStep("visuals loaded", () => visuals.IsLoaded)`), swap set, assert old disposed.
- [ ] **Step 2: Run — FAIL. Step 3: implement. Step 4: PASS. Step 5: Commit** `git commit -am "feat: now-playing visual stack (bg/video/storyboard)"`

---

### Task 10: Jukebox conductor (queue → cache → play → radio)

**Files:**
- Create: `JukeBox.Game/Playback/Jukebox.cs`
- Test: `JukeBox.Game.Tests/Playback/JukeboxTest.cs` (headless, fake mirror + temp cache)

**Interfaces:**
- Consumes: `MusicQueue`, `RadioService`, `BeatmapCache`, `PlaybackController`.
- Produces:
```csharp
public partial class Jukebox : Component
{
    public Jukebox(MusicQueue queue, RadioService radio, BeatmapCache cache, PlaybackController playback);
    public readonly Bindable<string?> LastError = new();
    public void Start();                    // begins radio if queue empty
    public Task EnqueueAndMaybePlayAsync(BeatmapSetInfo set); // enqueue; if idle → advance immediately
    public void SkipCurrent();
    // internal: OnTrackCompleted → AdvanceAsync():
    //   next = queue.PopNext() ?? await radio.PickRandomAsync();
    //   if null → LastError + retry radio after 5s (Scheduler.AddDelayed);
    //   cached = await cache.GetAsync(next.Id) (errors → LastError, skip to next);
    //   await playback.PlayAsync(cached); prefetch: if queue non-empty, fire-and-forget cache.GetAsync(head).
}
```

- [ ] **Step 1: Failing headless test** — enqueue two fixture sets (FileMirror), assert `playback.Current` becomes set 1, fire completion (expose `internal void Advance()` for tests or invoke via reflection—prefer making `AdvanceAsync` internal + `[assembly: InternalsVisibleTo("JukeBox.Game.Tests")]` in JukeBox.Game), assert set 2 plays, assert failing set → `LastError` set and next item plays.
- [ ] **Step 2–4: FAIL → implement → PASS.**
- [ ] **Step 5: Commit** `git commit -am "feat: jukebox conductor (queue/radio/prefetch/skip)"`

---

### Task 11: Search overlay + results UI

**Files:**
- Create: `JukeBox.Game/UI/SearchOverlay.cs`, `JukeBox.Game/UI/SearchResultRow.cs`
- Test: `JukeBox.Game.Tests/Visual/TestSceneSearchOverlay.cs`

**Interfaces:**
- Consumes: `IBeatmapMirror` (DI-resolved), `Jukebox.EnqueueAndMaybePlayAsync`.
- Produces:
```csharp
public partial class SearchOverlay : FocusedOverlayContainer
{
    public event Action<BeatmapSetInfo>? SetPicked;
    public void ShowWithInitialChar(char c);     // opens + seeds textbox (type-anywhere)
    // BasicTextBox top; results = FillFlowContainer<SearchResultRow> in BasicScrollContainer;
    // 300ms debounce via Scheduler.AddDelayed w/ cancel of previous ScheduledDelegate;
    // ↑/↓ move selection (OnKeyDown Key.Up/Down), Enter → SetPicked(selected ?? first), Hide();
    // Esc → Hide(). Search request: Status=ranked, Sort=ranked_desc, PageSize=30, Query=text.
}
public partial class SearchResultRow : ClickableContainer
{
    public SearchResultRow(BeatmapSetInfo set);
    public readonly BindableBool Selected = new();
    // thumb via OnlineStore texture from https://b.ppy.sh/thumb/{set.Id}l.jpg (best-effort),
    // texts: DisplayTitle — DisplayArtist / mapped by Creator, status text, [SB]/[VID] markers,
    // dimmed + non-clickable when set.DownloadDisabled.
}
```

Thumbnails: cache one `TextureStore(host.Renderer, host.CreateTextureLoaderStore(new OnlineStore()), useAtlas: false)` in GameBase as `OnlineTextureStore` (cached via `CacheAs`), rows call `onlineTextures.GetAsync($"https://b.ppy.sh/thumb/{set.Id}l.jpg")`.

- [ ] **Step 1: TestScene** with stub mirror returning 3 fixed sets: type "a" → `AddUntilStep("3 rows", ...)`; `AddStep("press enter", () => InputManager.Key(Key.Enter))` → assert `SetPicked` fired with first set. Use `ManualInputManagerTestScene`.
- [ ] **Step 2–4: FAIL → implement → PASS.**
- [ ] **Step 5: Commit** `git commit -am "feat: search overlay with live results"`

---

### Task 12: Now-playing bar + queue panel

**Files:**
- Create: `JukeBox.Game/UI/NowPlayingBar.cs`, `JukeBox.Game/UI/QueuePanel.cs`
- Test: `JukeBox.Game.Tests/Visual/TestSceneNowPlayingBar.cs`

**Interfaces:**
- Consumes: `PlaybackController` (`IsPlaying`, `CurrentTimeMs`, `LengthMs`, `Volume`, `TogglePause`, `Seek`), `Jukebox.SkipCurrent`, `MusicQueue.Items`.
- Produces:
```csharp
public partial class NowPlayingBar : CompositeDrawable
{
    // height 80, anchored bottom, full width: cover thumb, DisplayTitle/DisplayArtist SpriteTexts,
    // seekable progress bar (BasicSliderBar<double> bound to a BindableDouble progress, mouse-up → Seek),
    // BasicButtons: ⏯ (TogglePause), ⏭ (SkipCurrent); volume BasicSliderBar bound to Volume.
    // Update(): progress.Value = CurrentTimeMs / max(1, LengthMs) unless dragging.
}
public partial class QueuePanel : CompositeDrawable
{
    // right-anchored 320-wide panel; header "Queue (N)";
    // list bound to MusicQueue.Items (BindableList.BindCollectionChanged, rebuild rows);
    // per-row: title — artist + ✕ remove button (Items.Remove);
    // ToggleVisibility() slide in/out (this.MoveToX) — layout A drawer.
}
```

- [ ] **Step 1: TestScene:** bar against a stub-loaded PlaybackController fixture; assert play/pause button flips `IsPlaying`; queue panel shows rows after `queue.Enqueue`.
- [ ] **Step 2–4: FAIL → implement → PASS.**
- [ ] **Step 5: Commit** `git commit -am "feat: now-playing bar + queue panel"`

---

### Task 13: Settings + layouts + main screen wiring

**Files:**
- Create: `JukeBox.Game/Configuration/JukeBoxConfigManager.cs`, `JukeBox.Game/Screens/MainScreen.cs`
- Modify: `JukeBox.Game/JukeBoxGameBase.cs`, `JukeBox.Game/JukeBoxGame.cs`
- Test: `JukeBox.Game.Tests/Visual/TestSceneMainScreen.cs`

**Interfaces:**
- Consumes: everything above.
- Produces:
```csharp
public enum JukeBoxSetting { Volume, NoVideoDownloads, UiLayout, CacheSizeGb }
public enum UiLayout { FullscreenOverlay, Split }
public class JukeBoxConfigManager : IniConfigManager<JukeBoxSetting>
{
    public JukeBoxConfigManager(Storage storage) : base(storage) { }
    protected override void InitialiseDefaults()
    {
        SetDefault(JukeBoxSetting.Volume, 1.0, 0.0, 1.0);
        SetDefault(JukeBoxSetting.NoVideoDownloads, false);
        SetDefault(JukeBoxSetting.UiLayout, UiLayout.FullscreenOverlay);
        SetDefault(JukeBoxSetting.CacheSizeGb, 10.0);
    }
}
```

`JukeBoxGameBase.load()` (order matters):
```csharp
var http = new HttpClient(); // field, disposed with game
var mirror = new MirrorChain(new NerinyanMirror(http), new CatboyMirror(http), new OsuDirectMirror(http));
dependencies.CacheAs<IBeatmapMirror>(mirror);
config = new JukeBoxConfigManager(host.Storage);
dependencies.Cache(config);
var cache = new BeatmapCache(host.Storage.GetFullPath("cache"), mirror, config.Get<bool>(JukeBoxSetting.NoVideoDownloads));
dependencies.Cache(cache);
var queue = new MusicQueue(); dependencies.Cache(queue);
var radio = new RadioService(mirror); dependencies.Cache(radio);
AddInternal(playback = new PlaybackController()); dependencies.Cache(playback);
AddInternal(jukebox = new Jukebox(queue, radio, cache, playback)); dependencies.Cache(jukebox);
config.BindWith(JukeBoxSetting.Volume, playback.Volume);
```

`MainScreen` composes: `NowPlayingScreen` visuals area + `SearchOverlay` + `QueuePanel` + `NowPlayingBar` + layout switch:
- Layout A: visuals `RelativeSizeAxes = Both`; queue panel hidden by default (drawer); typing printable char (override `OnKeyDown` fallthrough: unhandled `KeyDownEvent` with printable char and no modifiers) → `search.ShowWithInitialChar(c)`.
- Layout B: `GridContainer` — left column 360px (search box + results + queue stacked, always visible), right cell visuals; same bottom bar.
- Tab key + a corner BasicButton toggle `config.GetBindable<UiLayout>(JukeBoxSetting.UiLayout)`; both layouts are two pre-built containers, toggle switches `Alpha`/`PropagateNonPositionalInputSubTree`.
- `jukebox.Start()` in `LoadComplete()` → radio kicks in on empty queue. `search.SetPicked += s => jukebox.EnqueueAndMaybePlayAsync(s)`.
- `jukebox.LastError.BindValueChanged(e => { if (e.NewValue != null) showToast(e.NewValue); })` — toast = auto-fading `SpriteText` container top-center, `FadeIn(200)` + `Delay(4000)` + `FadeOut(500)` + `Expire()`.

- [ ] **Step 1: TestScene:** MainScreen with stub mirror; type char → overlay visible; Tab → layout swaps (assert split container `Alpha == 1`).
- [ ] **Step 2–4: FAIL → implement → PASS.**
- [ ] **Step 5: Live smoke test:** `dotnet run --project JukeBox.Desktop` — search a real song (e.g. "megalovania"), Enter, verify: downloads, audio plays, background shows; find a storyboard map (search with results showing [SB]), verify storyboard renders; let track end with empty queue → radio picks something.
- [ ] **Step 6: Commit** `git commit -am "feat: wire main screen, layouts, settings, radio autostart"`

---

### Task 14: Cache LRU eviction + polish

**Files:**
- Modify: `JukeBox.Game/Beatmaps/BeatmapCache.cs`
- Test: extend `JukeBox.Game.Tests/Beatmaps/BeatmapCacheTest.cs`

**Interfaces:**
- Produces: `BeatmapCache.EvictToLimit(long maxBytes, IReadOnlyCollection<int> protectedIds)` — deletes least-recently-played set dirs (mtime of dir, touched on `GetAsync` hit) until under limit; never deletes `protectedIds`. `Jukebox` calls it after each successful `GetAsync` with current + queued ids, limit from `CacheSizeGb`.

- [ ] **Step 1: Failing test** — create 3 fake set dirs with sizes + staggered mtimes, evict to a limit that requires deleting the oldest, assert oldest gone, protected survives even if oldest.
- [ ] **Step 2–4: FAIL → implement → PASS.**
- [ ] **Step 5: Full suite + final smoke run.** `dotnet test JukeBox.Game.Tests` all green; `dotnet run --project JukeBox.Desktop` sanity.
- [ ] **Step 6: Commit** `git commit -am "feat: cache LRU eviction"`

---

## Self-review notes (done at plan time)

- Spec coverage: search (T3/T4/T11), download+cache (T5/T14), queue (T6/T12), radio (T6/T10), playback+clock (T7), storyboard (T8), video+bg (T9), two layouts+toggle+settings (T13), error toasts (T10/T13), mirror fallback for search AND download (T4), tests per component. Out-of-scope items from spec untouched.
- Known API-uncertainty points called out inline (Core `Vector` field casing, `Video` clock-offset sign, template class names) — implementer verifies at compile time rather than trusting the plan blindly.
- Type names consistent across tasks (`BeatmapSetInfo`, `CachedBeatmapSet`, `IBeatmapMirror`, `PlaybackController.PlaybackClock`, `Jukebox.EnqueueAndMaybePlayAsync`).
