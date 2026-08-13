# Third-party attribution

## osu!lazer (ppy.osu.Game + rulesets)

The gameplay "chart" display is rendered by [osu!lazer](https://github.com/ppy/osu)'s real
gameplay renderer, consumed as NuGet packages (`ppy.osu.Game`,
`ppy.osu.Game.Rulesets.{Osu,Taiko,Catch,Mania}`). osu!lazer's code is licensed under the
[MIT licence](https://github.com/ppy/osu/blob/master/LICENCE), Copyright (c) ppy Pty Ltd.

## osu!lazer resources (ppy.osu.Game.Resources)

Default skin assets, gameplay samples and fonts come from
[osu-resources](https://github.com/ppy/osu-resources) (`ppy.osu.Game.Resources`), which is
licensed under [CC-BY-NC 4.0](https://github.com/ppy/osu-resources/blob/master/LICENCE.md),
Copyright (c) ppy Pty Ltd. These assets are used here in a personal, non-commercial build.
"osu!" and related branding remain the property of ppy Pty Ltd.

## ReOsuStoryboardPlayer

Storyboard parsing/playback uses [ReOsuStoryboardPlayer](https://github.com/MikiraSora/ReOsuStoryboardPlayer)
(MIT), vendored as a git submodule under `external/`.
