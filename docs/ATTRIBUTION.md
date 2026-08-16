# Third-party attribution

osu!JukeBox's own source is MIT (see [`LICENSE`](../LICENSE)). The **published binaries** are a
different matter: they bundle third-party components, and two of them are not free for commercial
use. This file lists what ships and under what terms.

Licences below were read from each package's own `.nuspec`/`LICENSE` file in the restored
dependency tree, not from memory. 138 packages resolve into the desktop build; 101 declare plain
MIT and are not itemised individually.

## Not free for commercial use

These are the reason the distributed builds are **non-commercial**, even though the source is MIT.

### osu! resources (`ppy.osu.Game.Resources`)

Default skin assets, gameplay samples and fonts from
[osu-resources](https://github.com/ppy/osu-resources), licensed
[CC BY-NC 4.0](https://github.com/ppy/osu-resources/blob/master/LICENCE.md) — Copyright (c) ppy Pty
Ltd. The "NC" is non-commercial. "osu!" and its branding remain ppy's property.

### BASS (`libbass`, `libbass_fx`, `libbassmix`)

Audio playback uses [BASS](https://www.un4seen.com/) by un4seen developments, shipped as native
libraries next to the executable. BASS is proprietary and **free for non-commercial use only**;
commercial use requires a licence from un4seen. The C# wrapper around it (`ppy.ManagedBass`) is
separately MIT.

## Copyleft components

Shipped as separate dynamic libraries, replaceable by the user, and not statically linked into
this project's code.

| Component | Licence | Notes |
| --- | --- | --- |
| FFmpeg (`libavcodec`, `libavformat`, `libavutil`) | LGPL | Video decoding, via `FFmpeg.AutoGen` (MIT wrapper) |
| OpenTabletDriver (`.Configurations`, `.Native`, `.Plugin`) | LGPL-3.0-or-later | Pulled in by osu!framework |
| TagLibSharp | LGPL-2.1-only | Audio metadata |

## Other non-MIT components

| Component | Licence |
| --- | --- |
| SixLabors.ImageSharp | [Six Labors Split License 1.0](https://github.com/SixLabors/ImageSharp/blob/main/LICENSE) — free for open-source and small business; larger commercial use needs a paid licence |
| Realm, SQLitePCLRaw, DiffPlex, Microsoft.Extensions.ObjectPool | Apache-2.0 |
| Markdig | BSD-2-Clause |
| Veldrid, veldrid-spirv | MIT-style, per their own repos |

## MIT components worth naming

- **[osu!lazer](https://github.com/ppy/osu)** (`ppy.osu.Game` and the four ruleset packages) —
  Copyright (c) ppy Pty Ltd. This project hosts lazer's real gameplay, storyboard and video
  renderers rather than reimplementing them; it is the reason charts look correct.
- **[osu!framework](https://github.com/ppy/osu-framework)** — Copyright (c) ppy Pty Ltd. The game
  framework everything is drawn with.

## Beatmaps, skins and audio

Beatmaps are downloaded on demand from third-party mirrors —
[NeriNyan](https://nerinyan.moe/), [catboy.best](https://catboy.best/) and
[osu.direct](https://osu.direct/) — or located through osu!'s own API when credentials are
configured. Nothing is redistributed with this project.

Beatmaps, their audio, backgrounds, storyboards, videos and any imported skins remain the property
of their respective creators and rights holders.

## Not affiliated with ppy

osu!JukeBox is an unofficial fan project. It is not affiliated with, endorsed by, or supported by
ppy Pty Ltd.
