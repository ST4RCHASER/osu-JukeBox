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
    /// <summary>
    /// Presents the player visuals in their own OS window (a second process of the same binary —
    /// see Detach.DetachedViewerManager) instead of the main window's player box, which shows a
    /// placeholder while detached. Persisted like any other setting, so a user who parks the
    /// player on a second monitor gets it back on the next launch; closing the detached window
    /// flips this back off.
    /// </summary>
    DetachPlayer,
    /// <summary>
    /// Only meaningful while <see cref="DetachPlayer"/> is on: keeps the MAIN window's player
    /// box rendering the visuals as normal alongside the detached window, instead of showing
    /// the "playing in detached window" placeholder. Both windows run the same scene off the
    /// same sync feed, so this is a mirror, not a second player.
    /// </summary>
    DetachPlayOnMain,
    /// <summary>
    /// Directory the in-app file picker (<see cref="UI.FileImportOverlay"/>) last browsed, so
    /// reopening it lands where the user left off rather than back at the default. Empty (the
    /// default) means "no remembered directory" — see <c>FileImportOverlay.ResolveInitialPath</c>
    /// for the fallback chain, which also covers a remembered directory that has since been
    /// deleted or moved.
    /// </summary>
    LastImportDirectory,
    /// <summary>
    /// Folder NAME (not a full path) of the user-imported legacy skin under the app storage's
    /// <c>skins/</c> directory — written when a .osk is dragged onto the window (see
    /// <see cref="Import.SkinArchive"/>), read by <see cref="LazerPlayer.SkinSelection"/> whenever
    /// <see cref="Skin"/> is <see cref="JukeBoxSkin.Custom"/>. A name rather than an absolute path
    /// so the choice survives the storage directory itself moving (a different OS user, a portable
    /// install). Empty means nothing has been imported, in which case Custom degrades to Argon.
    /// </summary>
    CustomSkinPath,

    /// <summary>
    /// Which backend answers beatmap SEARCHES (see <see cref="Online.SearchApi"/>). Downloads are
    /// unaffected and always go through <see cref="PreferredMirror"/>'s chain.
    /// </summary>
    SearchApi,

    /// <summary>
    /// The user's own osu! OAuth application client id, used only when <see cref="SearchApi"/> is
    /// <see cref="Online.SearchApi.Official"/>. Per-user rather than bundled with the app: osu!'s
    /// docs are explicit that a client secret must not be shared, and a secret shipped inside an
    /// open-source binary is published to everyone who downloads it.
    /// </summary>
    OsuClientId,

    /// <summary>
    /// The matching client secret. Stored in the plain-text config like every other setting — it
    /// grants the <c>public</c> scope only (no user data, no writes), but it is still the user's
    /// credential, so it is masked in the settings UI and never written to the log.
    /// </summary>
    OsuClientSecret,

    /// <summary>
    /// The gameplay mods the Chart tab applies to the autoplay chart, as a comma-separated list of
    /// osu! acronyms ("HD,HR,DT"). Stored as acronyms rather than one boolean key per mod so the
    /// set can grow without churning this enum — and because an acronym is exactly how osu! itself
    /// names a mod, which keeps the persisted value readable and stable across upgrades. Unknown
    /// acronyms are ignored on load. Empty (the default) is no mods.
    ///
    /// Ignored entirely while a dropped replay is driving playback: a replay carries its OWN mods
    /// (see <see cref="Replays.ReplayMods"/>), and the Chart tab's toggles lock in that state.
    /// </summary>
    ChartMods,

    /// <summary>
    /// Which playfield elements the user has switched OFF, as a comma-separated list of
    /// <see cref="LazerPlayer.PlayfieldElement"/> names. Stored as a "hidden" list (rather than a
    /// "shown" one) so everything a future version adds defaults to visible, and by NAME so
    /// reordering the enum can never silently repoint a user's choices. Unknown names are ignored
    /// on load. Empty (the default) is "everything visible".
    /// </summary>
    HiddenPlayfieldElements,

    /// <summary>
    /// Which ruleset the chart is RENDERED as regardless of the mode its .osu declares — osu!'s own
    /// "play a standard map as another mode" conversion. See
    /// <see cref="LazerPlayer.ChartConversionTarget"/>; <c>Off</c> (the default) plays every map in
    /// its own mode.
    ///
    /// Global rather than per-beatmap on purpose: it describes how the user wants to listen, not a
    /// property of any one map (see <see cref="LazerPlayer.ChartConversion"/>).
    /// </summary>
    ConvertToRuleset,

    /// <summary>
    /// Show what's playing on the user's Discord profile (see
    /// <see cref="Presence.DiscordPresenceService"/>). On by default — it publishes only beatmap
    /// metadata that is already public, and with no Discord running it does nothing at all.
    ///
    /// Deliberately absent from <see cref="Detach.SettingsMirror"/>: that registry exists for
    /// settings that change what the player RENDERS, and presence is published by the main process
    /// alone (the viewer must never open a second Discord connection), so mirroring it would send
    /// the viewer a setting it has no use for.
    /// </summary>
    DiscordRichPresence,
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
/// <see cref="Random"/> re-rolls one of the four concrete skins on every song change, and
/// <see cref="Custom"/> selects whatever legacy skin the user last imported by dropping a .osk
/// (see <see cref="JukeBoxSetting.CustomSkinPath"/>).
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

    /// <summary>
    /// The user's imported .osk, resolved through <see cref="JukeBoxSetting.CustomSkinPath"/>.
    /// Selectable with nothing imported (the dropdown lists every enum member), in which case it
    /// degrades to <see cref="Argon"/> rather than rendering nothing — see
    /// <c>SkinSelection.CreateEffectiveSkin</c>. Never rolled by <see cref="Random"/>: a random
    /// skin should stay predictable across machines, and it may not resolve at all.
    /// </summary>
    [System.ComponentModel.Description("Custom (imported)")]
    Custom,
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
        SetDefault(JukeBoxSetting.DetachPlayer, false);
        SetDefault(JukeBoxSetting.DetachPlayOnMain, false);
        SetDefault(JukeBoxSetting.LastImportDirectory, string.Empty);
        SetDefault(JukeBoxSetting.CustomSkinPath, string.Empty);

        // Mirror by default: the official API needs credentials the user has to create themselves,
        // so anything else would leave a fresh install unable to search at all.
        SetDefault(JukeBoxSetting.SearchApi, SearchApi.Mirror);
        SetDefault(JukeBoxSetting.OsuClientId, string.Empty);
        SetDefault(JukeBoxSetting.OsuClientSecret, string.Empty);

        SetDefault(JukeBoxSetting.ChartMods, string.Empty);
        SetDefault(JukeBoxSetting.HiddenPlayfieldElements, string.Empty);
        SetDefault(JukeBoxSetting.ConvertToRuleset, LazerPlayer.ChartConversionTarget.Off);
        SetDefault(JukeBoxSetting.DiscordRichPresence, true);
    }
}
