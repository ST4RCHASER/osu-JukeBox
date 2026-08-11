# osu!JukeBox — Design

Date: 2026-08-11
Status: Approved by user (pre-implementation)

## Purpose

A standalone desktop jukebox for osu! music. Opens to an empty queue; typing searches the NeriNyan beatmap mirror; pressing Enter queues a song. Songs play queue-by-queue; when the queue is empty, the app plays random ranked songs from NeriNyan (endless radio). While a song plays, the app renders its storyboard (if present) and background video (if present), like osu! gameplay visuals.

Built on osu!framework (ppy), reusing `ReOsuStoryboardPlayer.Core` as the storyboard engine.

## Decisions locked with the user

- **Song source**: download `.osz` from NeriNyan on demand, extract into the app's own local cache. No osu! install required.
- **Radio**: random songs come from NeriNyan (not the local cache), so radio works on a fresh install.
- **Storyboard engine**: `ReOsuStoryboardPlayer.Core` evaluates; osu!framework draws. Core computes per-frame sprite state via `StoryboardUpdater`; the app maps that state onto framework sprites each frame. No command-to-transform compilation.
- **UI**: two layouts, toggleable at runtime and persisted — (A) fullscreen visuals with overlay UI, (B) split layout with a permanent left panel.
- **Repo**: new sibling folder `~/Work/osu-JukeBox`, its own git repository.
- **Platform**: macOS first (Veldrid/Metal via osu!framework); Windows later for free.

## 1. Projects

osu!framework template layout, all `net10.0` (the framework's `net8.0` package is consumable from net10; template csprojs get bumped):

- **`JukeBox.Game`** — all logic and UI. References `ppy.osu.Framework` (2026.807.0 or newer) and `ReOsuStoryboardPlayer.Core`.
- **`JukeBox.Desktop`** — thin entry point (`Host.GetSuitableDesktopHost("osu!JukeBox")`).
- **`JukeBox.Resources`** — embedded fonts and UI samples.
- **`JukeBox.Game.Tests`** — interactive TestBrowser + headless NUnit tests.

`ReOsuStoryboardPlayer.Core` is consumed as a **git submodule** of the ReOsuStoryboardPlayer repo + `ProjectReference` to the Core csproj (the published NuGet package predates the net10/AOT work).

Every Drawable subclass is `partial` (osu!framework source generators).

## 2. Services (cached in `JukeBoxGameBase` via DI)

### `BeatmapMirrorClient` (interface `IBeatmapMirror`)

Ordered mirror list, applied to both search and download: **NeriNyan → catboy.best → osu.direct**.

NeriNyan specifics (verified live 2026-08-11):
- Search: legacy `GET https://api.nerinyan.moe/search` with `q`, `s` (status, default `ranked`), `m`, `sort`, `p`, `ps`, `e` (`storyboard` / `video` / `video.storyboard`), `option`. Range filters via `?b64=<base64 JSON body>` (`POST /search` is broken — always 400). Do **not** use `/v2/search` (no storyboard/video filters). Response: bare JSON array of beatmapsets with `id, title, artist, creator, status, video, storyboard, availability{download_disabled}, beatmaps[]` etc.
- **Pagination hard cap**: `p * ps < 10000`; exceeding it returns HTTP 500 (not an empty list). Clamp before requesting.
- Download: `GET https://dl.nerinyan.moe/v2/d/{setId}` (skip the `api.` 302). `content-disposition` carries the filename. Optional `nv=1` strips video (~45% smaller on video maps) — honored from settings. Never pass `ns`/`nb` (storyboard and backgrounds are needed). Downloads are resumable (302 to presigned S3 URL supporting `Range`; URL expires in 1 h — on resume, re-request from `dl`, never cache the S3 link; `HEAD` returns 405).
- Backgrounds: `https://dl.nerinyan.moe/v2/bg/{setId}` (the `api.nerinyan.moe/bg/` path is broken). Covers/thumbnails built from osu! CDN: `https://assets.ppy.sh/beatmaps/{setId}/covers/cover.jpg`, `https://b.ppy.sh/thumb/{setId}l.jpg`.
- No auth, no observed rate limits, no UA requirement — still throttle politely (single concurrent download, debounced search).

Fallback mirrors: catboy.best (`/api/v2/search`, `/d/{setId}`), osu.direct (`/api/v2/search`, `/api/d/{setId}`). Their search APIs lack the storyboard filter — accepted degradation; the `storyboard` flag is still present per result.

### `BeatmapCache`

- App storage (osu!framework `Storage`): `cache/{setId}/` holds the extracted `.osz` contents; `cache/index.json` records set metadata (id, title, artist, has video/storyboard, size, last-played).
- Extracts with `System.IO.Compression.ZipFile`. In-flight downloads are deduplicated (second enqueue of the same set awaits the same task).
- LRU eviction against a configurable cache-size cap; never evicts the currently playing or queued sets.

### `MusicQueue`

- `BindableList<QueueItem>` (`QueueItem` = beatmapset metadata + cache state). Enqueue triggers download-ahead (next 1–2 items pre-fetched). Head pops to the player when the current track ends or is skipped.

### `RadioService`

- Activates when a song ends and the queue is empty. Picks a random ranked set from NeriNyan: random page within the 10k window (`ps=50`, `p` ∈ 0–199) with a randomized sort axis/direction to widen the reachable pool; optional `b64` random BPM/star window as a second axis. Skips sets with `availability.download_disabled`. Retries with a fresh pick on download failure (max 3, then surfaces an error toast and idles).

### `PlaybackController`

- Loads the cached audio file as a framework `Track` (track store over a `StorageBackedResourceStore` for the cache folder).
- `Track : IAdjustableClock` wrapped in `DecouplingFramedClock` → assigned as `Clock` of the entire visual subtree; storyboard, video, and progress UI all read the same clock.
- `Track.Completed` → advance queue (or radio). Public surface: play/pause, skip, seek, volume (bindables).

## 3. Visual stack (`NowPlayingScreen`)

One container in 640×480 storyboard space, `DrawScale = DrawHeight / 480`, widescreen maps get the 854-wide variant (per the beatmap's `WidescreenStoryboard` flag). Layered bottom-up:

1. **Background image** — from the extracted map (parsed from the `.osu` background event); fallback `dl.nerinyan.moe/v2/bg/{setId}`.
2. **Video** — osu!framework `Video` drawable when the map declares one and the file exists (skipped when the no-video download setting stripped it). Runs on the shared clock with the video's start-time offset. Decode failure hides the layer.
3. **StoryboardLayer** — the Core integration:
   - Parse `.osb` + the selected `.osu` (first `Mode: 0` difficulty, matching ReOsuStoryboardPlayer's convention; if the set has no osu!std difficulty, the first difficulty of any mode) with Core's reader chain; merge per layer (.osu first, then .osb) and assign Z; `CalculateAndApplyBaseFrameTime()` on every object; optimize at level 2 (`RuntimeOptimzer` registered).
   - Per frame in `Update()`: `StoryboardUpdater.Update(Clock.CurrentTime)`, then map each object in `UpdatingStoryboardObjects` (already Z-sorted) to a pooled framework `Sprite`: `Postion`→Position, `Rotate`→Rotation, `Scale`/flips→Scale (sign for flips), `Color`→Colour+Alpha, `IsAdditive`→Blending, `OriginOffset`/Anchor→Origin, list order→depth.
   - Textures: `TextureStore(useAtlas: false, scaleAdjust: 1)` over the map folder; animation frames resolved per Core's `ImageFilePath` (it swaps the path per frame).
   - Seeking is free — Core's updater handles backward jumps via `Flush()`.
   - Hitsound-driven `Trigger` commands are out of scope for v1 (the feed is inert in the reference player too).
4. **UI overlay** (outside the 640×480 container, window space).

Loading a beatmapset happens off the update thread (`LoadComponentAsync` for the whole visual stack); a lightweight spinner shows during download+parse.

## 4. UI

Two layouts toggled by a button and hotkey (Tab), persisted:

- **Layout A — fullscreen visuals**: visuals fill the window. Typing any printable character opens the search overlay (top drop-down: text box + live results, 300 ms debounce). ↑/↓ select, Enter enqueues (and starts playback if idle), Esc closes. Right-side queue drawer (toggle). Bottom now-playing bar: cover thumbnail, title/artist (unicode fields preferred), progress bar (seekable), play/pause, skip, volume.
- **Layout B — split**: fixed left panel with search box, results list, and the queue always visible; right side is the visual viewport; same bottom bar.

Search results show: cover thumb (osu! CDN), title — artist, creator, status badge, video/storyboard icons. Global type-to-search implemented with a focused-by-default hidden textbox in layout A.

## 5. Error handling

- Download failure → next mirror in order; all mirrors fail → toast + item skipped (queue advances; radio re-picks).
- Search failure/timeout → fall back to next mirror's search; page-cap 500s prevented by clamping.
- `availability.download_disabled` → excluded from radio picks; shown disabled in search results.
- Missing storyboard → background + video only. Missing/failing video → layer hidden. Malformed storyboard (Core parse throw) → log, drop storyboard layer, keep audio playing.
- Corrupt `.osz` / extraction failure → cache entry purged, mirror retry once, then skip.

## 6. Testing

- **TestScenes** (interactive + CI): search overlay against a stubbed `IBeatmapMirror`; queue flow (enqueue → download-ahead → advance) with a local fixture `.osz`; `StoryboardLayer` against a fixture map with known storyboard (assert sprite state at fixed clock times via `ManualClock`).
- **Headless NUnit**: mirror client JSON parsing (recorded fixtures per mirror), b64 body encoding, pagination clamp, cache extraction + LRU eviction, radio picker constraints (never picks download-disabled).

## 7. Settings (framework `ConfigManager`)

- Volume (master).
- No-video downloads (`nv=1`) — default off.
- Layout (A/B) — persisted toggle.
- Cache size cap (default 10 GB) with LRU eviction.

## Out of scope (v1)

Playlists, osu! account login, per-difficulty selection UI, hitsound-triggered storyboard triggers, collections import, mobile targets, Windows testing (build stays cross-platform, verification deferred).

## Reference notes

- Full repo scan of ReOsuStoryboardPlayer and osu!framework API research: see session report (`reosb-deep-scan.md`).
- NeriNyan endpoint behavior verified live on 2026-08-11; OpenAPI spec snapshot at `/tmp/nerinyan_spec.json` during research.
- osu!framework gotchas honored: `partial` drawables, transforms only after `LoadComplete`, `CacheAs<T>` for interface caching, `scaleAdjust: 1` for storyboard textures, BASS commercial licensing (fine for personal/non-commercial use).
