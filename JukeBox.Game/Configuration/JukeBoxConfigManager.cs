using JukeBox.Game.Online;
using osu.Framework.Configuration;
using osu.Framework.Platform;

namespace JukeBox.Game.Configuration;

public enum JukeBoxSetting
{
    /// <summary>
    /// Deprecated: superseded by the framework's master volume
    /// (<see cref="osu.Framework.Configuration.FrameworkSetting.VolumeUniversal"/>), which every
    /// audio component (tracks, storyboard samples, chart hitsounds) already multiplies in.
    /// Kept only so old ini values still parse; its value is copied into the framework setting
    /// once, guarded by <see cref="VolumeMigrated"/>. See JukeBoxGameBase.load.
    /// </summary>
    Volume,
    NoVideoDownloads,
    UiLayout,
    CacheSizeGb,
    /// <summary>
    /// Deprecated: superseded by <see cref="FpsDisplay"/>, which distinguishes a compact overlay
    /// from the full frame-time graph rather than a single on/off toggle. Kept only so old ini
    /// values still parse; its value is copied into <see cref="FpsDisplay"/> once, guarded by
    /// <see cref="FpsDisplayMigrated"/>. See JukeBoxGameBase.load.
    /// </summary>
    ShowFps,
    /// <summary>
    /// Deprecated: superseded by <see cref="FpsDisplayMode"/>. Kept ONLY so old
    /// Off/Compact/Details ini text (see <see cref="Game.Configuration.LegacyFpsDisplayMode"/>)
    /// still decodes under its ORIGINAL meaning — <see cref="FpsDisplayMode"/> reuses the
    /// "Compact"/"Details" NAMES for different meanings, and osu.Framework's ini loader matches
    /// persisted enum values by name, so decoding old text straight into the new type would
    /// silently misinterpret it. Its value is copied into <see cref="FpsDisplayMode"/> once,
    /// guarded by <see cref="FpsDisplayModeMigrated"/>. See JukeBoxGameBase.load and
    /// JukeBoxGameBase.MigrateLegacyFpsDisplay.
    /// </summary>
    FpsDisplay,
    /// <summary>One-shot guard for the <see cref="ShowFps"/> → <see cref="FpsDisplay"/> (legacy) migration.</summary>
    FpsDisplayMigrated,
    PreferredMirror,
    RenderChart,
    PlayHitSounds,
    BackgroundDim,
    /// <summary>One-shot guard for the <see cref="Volume"/> → VolumeUniversal migration.</summary>
    VolumeMigrated,
    Skin,
    BackgroundBlur,
    ShowStoryboardVideo,
    UiScale,
    /// <summary>Global audio offset in ms, added to the per-beatmap offset (BeatmapOffsetStore).</summary>
    GlobalAudioOffset,
    /// <summary>Replaces the legacy <see cref="FpsDisplay"/> shape: Off / Compact / Details / Graph,
    /// see <see cref="Game.Configuration.FpsDisplayMode"/> for what each means.</summary>
    FpsDisplayMode,
    /// <summary>One-shot guard for the <see cref="FpsDisplay"/> (legacy) → <see cref="FpsDisplayMode"/> migration.</summary>
    FpsDisplayModeMigrated,
    /// <summary>
    /// Uniform scale applied to <see cref="Screens.MainScreen"/>'s own sceneContainer (the fixed
    /// design-canvas host of the ENTIRE visuals stack — background, storyboard/video, and chart
    /// together — that <c>updateSceneScale</c> already aspect-fits into <c>playerBox</c> every
    /// frame; this factor multiplies into that same computed scale), not anything inside
    /// BeatmapVisuals — 1.0 is the nominal (unzoomed) size. Below 1.0 shrinks the whole scene;
    /// above 1.0 magnifies it. <c>playerBox</c>'s own masking is what clips it at the box's edges
    /// either way — nothing here relaxes or extends that. See MainScreen.updateSceneScale.
    ///
    /// Renamed from the short-lived ChartZoom (chart-only scale, applied inside BeatmapVisuals
    /// instead — see that type's git history) directly, without a migration path: ChartZoom was
    /// never in a released build (added and reworked within the same development cycle), so there
    /// is no real persisted value anywhere that would need remapping — unlike e.g. <see cref="Volume"/>
    /// or the FpsDisplay pair above, which DO carry real legacy ini values and so keep their old key
    /// around as a one-shot migration source.
    /// </summary>
    PlayfieldZoom,
    /// <summary>How beatmap search results are presented — see <see cref="Configuration.SearchStyle"/>.</summary>
    SearchStyle,
}

/// <summary>
/// Beatmap search presentation. <see cref="Compact"/> (the default) keeps everything in the
/// docked left column, tightened: dense 48px-thumb card rows, filters collapsed by default,
/// smaller chips. <see cref="Fullscreen"/> additionally presents an osu!-web-style "beatmap
/// listing" overlay covering the player-box area whenever search is opened (type-anywhere, or
/// focusing the docked search box) — a large keyword box, the full labelled filter block and a
/// three-column card grid with hover-expanded cards; Escape or Enter-queue closes it back to the
/// player. Both presentations are views over the same shared <see cref="Online.BeatmapSearchEngine"/>,
/// so query/filter state stays in sync and the setting is live-switchable.
/// </summary>
public enum SearchStyle
{
    Fullscreen,
    Compact,
}

/// <summary>
/// Pre-Compact-overlay/Graph-rename shape of the FPS setting: <see cref="Off"/>, <see cref="Compact"/>
/// (the framework's single-line Minimal counter) and <see cref="Details"/> (the framework's full
/// frame-time Graph). Kept ONLY to decode legacy <see cref="JukeBoxSetting.FpsDisplay"/> ini text
/// during migration (JukeBoxGameBase.MigrateLegacyFpsDisplay) — <see cref="FpsDisplayMode"/> reuses
/// "Compact"/"Details" as names for different meanings, and osu.Framework's ini loader matches
/// persisted enum values by name, so this separate type is what lets the old text still decode
/// under its original meaning instead of being silently reinterpreted.
/// </summary>
internal enum LegacyFpsDisplayMode
{
    Off,
    Compact,
    Details,
}

/// <summary>
/// FPS overlay presentation. <see cref="Off"/> → nothing. <see cref="Compact"/> → a small custom
/// corner overlay (JukeBoxGameBase's own drawable sampling the update/draw clocks; the framework's
/// own <see cref="osu.Framework.Graphics.Performance.FrameStatisticsMode"/> stays None for this
/// mode). <see cref="Details"/> → the framework's single-line Minimal counter. <see cref="Graph"/>
/// → the framework's full frame-time Graph. See JukeBoxGameBase.FrameStatisticsModeFor.
///
/// Replaces the legacy <see cref="JukeBoxSetting.FpsDisplay"/>/<see cref="LegacyFpsDisplayMode"/>
/// shape, whose Compact/Details member NAMES this enum reuses for different meanings — old
/// "Compact" (old Minimal) now means <see cref="Details"/> here, and old "Details" (old Full) now
/// means <see cref="Graph"/>. See JukeBoxGameBase.MigrateLegacyFpsDisplay for the migration that
/// remaps old values instead of letting them silently re-parse under the new meanings.
/// </summary>
public enum FpsDisplayMode
{
    Off,
    Compact,
    Details,
    Graph,
}

/// <summary>
/// The bundled lazer skin driving the gameplay chart renderer. All four concrete entries are the
/// skins constructible without a realm-backed SkinManager in ppy.osu.Game 2026.730.0 (each has an
/// IStorageResourceProvider-only constructor); there is no bundled "retro" skin in that package.
/// <see cref="Random"/> re-rolls one of the four concrete skins on every song change.
/// </summary>
public enum JukeBoxSkin
{
    Argon,

    [System.ComponentModel.Description("Argon Pro")]
    ArgonPro,

    Triangles,
    Classic,

    [System.ComponentModel.Description("<Random Skin>")]
    Random,
}

/// <summary>
/// The main-screen layout mode. <see cref="ThreeColumn"/> (the default) shows the permanently
/// docked search/queue-and-settings columns either side of the visuals; <see cref="Focus"/> hides
/// both columns for a full-bleed view (toggled with Tab, restoring the previous mode on a second
/// press).
///
/// Replaces the old FullscreenOverlay/Split pair from the two-layout-toggle design. Renamed rather
/// than reusing those names since the semantics changed (Split kept a permanent left panel with a
/// floating listing/queue drawer; ThreeColumn is the new fixed three-column shell). Any old ini
/// value ("FullscreenOverlay" or "Split") fails <c>Enum.Parse</c> during the framework's own
/// per-setting config load (caught and discarded there), so a config written by a previous version
/// simply falls back to the freshly-declared default below — <see cref="ThreeColumn"/> — rather
/// than throwing or silently resurrecting a layout mode that no longer exists.
/// </summary>
public enum UiLayout
{
    ThreeColumn,
    Focus,
}

public class JukeBoxConfigManager : IniConfigManager<JukeBoxSetting>
{
    public JukeBoxConfigManager(Storage storage)
        : base(storage)
    {
    }

    protected override void InitialiseDefaults()
    {
        SetDefault(JukeBoxSetting.Volume, 1.0, 0.0, 1.0);
        SetDefault(JukeBoxSetting.NoVideoDownloads, false);
        SetDefault(JukeBoxSetting.UiLayout, UiLayout.ThreeColumn);
        SetDefault(JukeBoxSetting.CacheSizeGb, 10.0);
        SetDefault(JukeBoxSetting.ShowFps, false);
        SetDefault(JukeBoxSetting.FpsDisplay, LegacyFpsDisplayMode.Off);
        SetDefault(JukeBoxSetting.FpsDisplayMigrated, false);
        SetDefault(JukeBoxSetting.FpsDisplayMode, FpsDisplayMode.Off);
        SetDefault(JukeBoxSetting.FpsDisplayModeMigrated, false);
        SetDefault(JukeBoxSetting.PreferredMirror, MirrorSource.Auto);
        SetDefault(JukeBoxSetting.RenderChart, false);
        SetDefault(JukeBoxSetting.PlayHitSounds, false);
        SetDefault(JukeBoxSetting.BackgroundDim, 0.3, 0.0, 1.0);
        SetDefault(JukeBoxSetting.VolumeMigrated, false);
        SetDefault(JukeBoxSetting.Skin, JukeBoxSkin.Argon);
        SetDefault(JukeBoxSetting.BackgroundBlur, 0.0, 0.0, 1.0);
        SetDefault(JukeBoxSetting.ShowStoryboardVideo, true);
        SetDefault(JukeBoxSetting.UiScale, 1.0, 0.8, 1.6);
        SetDefault(JukeBoxSetting.GlobalAudioOffset, 0.0, -250.0, 250.0);
        SetDefault(JukeBoxSetting.PlayfieldZoom, 1.0, 0.01, 2.0);
        SetDefault(JukeBoxSetting.SearchStyle, SearchStyle.Compact);
    }
}
