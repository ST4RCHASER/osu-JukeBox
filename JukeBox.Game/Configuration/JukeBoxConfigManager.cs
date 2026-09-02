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
    /// <summary>
    /// Deprecated: superseded by the independent <see cref="ShowStoryboard"/> and
    /// <see cref="ShowVideo"/>, which the user asked to be able to switch separately (one combined
    /// toggle cannot leave a map's video playing while silencing a busy storyboard, or the
    /// reverse). Kept only so old ini values still parse; its value is copied into BOTH new keys
    /// once, guarded by <see cref="StoryboardVideoSplitMigrated"/> — someone who had the combined
    /// toggle off must find both halves off rather than silently back on. See JukeBoxGameBase.load.
    /// </summary>
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
    /// Folder NAME (not a full path) of the SELECTED imported skin under the app storage's
    /// <c>skins/</c> directory — written when a .osk is dragged onto the window (see
    /// <see cref="Import.SkinArchive"/>) or when one is picked out of the settings dropdown, read
    /// by <see cref="LazerPlayer.SkinSelection"/> whenever <see cref="Skin"/> is
    /// <see cref="JukeBoxSkin.Custom"/>. A name rather than an absolute path so the choice survives
    /// the storage directory itself moving (a different OS user, a portable install). Empty means
    /// nothing has been imported, in which case Custom degrades to Argon.
    ///
    /// <para>
    /// This names ONE skin out of however many are installed. The library itself is not stored
    /// here, because the <c>skins/</c> directory already is the library (see
    /// <see cref="LazerPlayer.SkinLibrary"/>) — which is also why the persisted identity is the
    /// folder rather than the skin's display name: names come out of each skin's own skin.ini, and
    /// two installed skins can perfectly well declare the same one.
    /// </para>
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

    /// <summary>
    /// Lets the rendered CHART (playfield) draw past the player box's edges instead of being
    /// clipped at them — the box's mask is what normally crops a ruleset's own overflow (catch's
    /// fruit spawn line and catcher, taiko's post-hit fly-up, a zoomed-in playfield) once it
    /// reaches the box edge. Off (the default) is the boxed behaviour every previous version had.
    ///
    /// Only the chart is released: the background, the dim scrim and the storyboard keep clipping
    /// exactly where they did (see <c>BeatmapVisuals</c>'s per-layer clips), and the released
    /// chart still draws BEHIND the side columns, which sit above the player in the screen's own
    /// child order.
    /// </summary>
    RemoveChartMask,

    /// <summary>
    /// The same release for the STORYBOARD (and its video), which is the one most often asked for:
    /// widescreen storyboards are authored wider than the 4:3 play area and lazer renders them
    /// that way, so the box's edges crop content their authors meant to be seen. Independent of
    /// <see cref="RemoveChartMask"/> — each layer has its own clip.
    /// </summary>
    RemoveStoryboardMask,

    /// <summary>
    /// How opaque the rendered chart is, 0-1 (1 = fully opaque, the default). Applied as alpha
    /// straight onto the live gameplay layer, so it takes effect on the chart already on screen
    /// with no rebuild — unlike mods or a conversion, which change what the layer IS.
    ///
    /// Only meaningful while <see cref="RenderChart"/> is on; with rendering off the layer is
    /// hidden outright (and kept alive only when <see cref="PlayHitSounds"/> wants its audio).
    /// Hitsounds are independent of this value: a chart at 0% still plays them, exactly as a
    /// hidden one does.
    /// </summary>
    ChartOpacity,

    /// <summary>
    /// Whether running out of queue starts the radio (a random song) instead of simply stopping.
    /// On by default, which is what the app has always done. Off makes the queue authoritative: the
    /// last queued song ends and nothing follows it — no lookup, and no failure to report, since
    /// not searching cannot fail. See <see cref="Playback.Jukebox"/>.
    /// </summary>
    RadioOnEmptyQueue,

    /// <summary>
    /// Whether launching with an empty queue starts the radio straight away, rather than waiting
    /// for the user to press play or next. On by default — this is not a new behaviour but a switch
    /// over an existing one, since the app has always started a random song at launch (MainScreen
    /// starts the jukebox unconditionally, and the queue is never restored across launches).
    ///
    /// <para>
    /// Independent of <see cref="RadioOnEmptyQueue"/> rather than gated by it: they answer
    /// different questions ("should the app greet me with music?" versus "should music keep coming
    /// after my queue runs dry?"), and a user who wants exactly one radio song at launch and
    /// silence thereafter has no way to say so if one implies the other. So this fires one radio
    /// pick even with <see cref="RadioOnEmptyQueue"/> off.
    /// </para>
    /// </summary>
    RadioOnStart,

    /// <summary>Ruleset the radio's picks must be playable in; <see cref="RadioRuleset.Any"/> = no
    /// mode filter. See <see cref="Playback.RadioFilters"/> for the set as a whole.</summary>
    RadioMode,

    /// <summary>Ranked status the radio picks from, as lazer's own Categories value.</summary>
    RadioCategory,

    /// <summary>Genre the radio picks from, as lazer's <c>SearchGenre</c> (whose values ARE
    /// osu-web's genre ids). Official backend only — no mirror search can express it.</summary>
    RadioGenre,

    /// <summary>Language the radio picks from, as lazer's <c>SearchLanguage</c>. Official backend
    /// only, for the same reason as <see cref="RadioGenre"/>.</summary>
    RadioLanguage,

    /// <summary>Restricts the radio to sets with a video ("Extra" row).</summary>
    RadioHasVideo,

    /// <summary>Restricts the radio to sets with a storyboard ("Extra" row).</summary>
    RadioHasStoryboard,

    /// <summary>Lower bound of the radio's star-rating band; 0 = no lower bound.</summary>
    RadioMinStars,

    /// <summary>Upper bound of the radio's star-rating band; 10 = no upper bound.</summary>
    RadioMaxStars,

    /// <summary>Restricts the radio to osu!'s Featured Artist library. Official backend only (see
    /// <see cref="Online.SearchFilters.FeaturedArtists"/>).</summary>
    RadioFeaturedArtists,

    /// Whether the storyboard is drawn — the sprites, animations and their Sample events, but NOT
    /// the storyboard's video, which is <see cref="ShowVideo"/>'s. Off silences the storyboard's
    /// samples too: a hidden layer that is still playing keysounds is a hidden layer you can hear.
    /// Replaces half of the deprecated <see cref="ShowStoryboardVideo"/>.
    /// </summary>
    ShowStoryboard,

    /// <summary>
    /// Whether the storyboard's VIDEO is drawn. Independent of <see cref="ShowStoryboard"/>: in
    /// lazer's model the video is one more storyboard layer, and this switches exactly that layer,
    /// which is what makes "video without storyboard" (and the reverse) possible at all.
    /// Replaces the other half of the deprecated <see cref="ShowStoryboardVideo"/>.
    /// </summary>
    ShowVideo,

    /// <summary>One-shot guard for the <see cref="ShowStoryboardVideo"/> →
    /// <see cref="ShowStoryboard"/> + <see cref="ShowVideo"/> split.</summary>
    StoryboardVideoSplitMigrated,

    /// <summary>
    /// Which storyboard layers the user has switched OFF, as a comma-separated list of
    /// <see cref="LazerPlayer.StoryboardLayerKind"/> names — same list-shaped, "persist the hidden
    /// ones" scheme as <see cref="HiddenPlayfieldElements"/>, and for the same reasons. Empty (the
    /// default) is "every layer drawn".
    /// </summary>
    HiddenStoryboardLayers,
}

/// <summary>
/// The radio's "Mode" filter. Our own enum rather than lazer's <c>RulesetInfo</c> (a realm model,
/// with no "any" member) or a bare ruleset int, so the persisted value is a readable name that
/// can't be confused with "osu!std" the way a defaulted 0 would be.
/// </summary>
public enum RadioRuleset
{
    Any = -1,

    [System.ComponentModel.Description("osu!")]
    Osu = 0,

    [System.ComponentModel.Description("osu!taiko")]
    Taiko = 1,

    [System.ComponentModel.Description("osu!catch")]
    Catch = 2,

    [System.ComponentModel.Description("osu!mania")]
    Mania = 3,
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
/// The KIND of skin driving the gameplay chart renderer. The four bundled entries are the skins
/// constructible without a realm-backed SkinManager in ppy.osu.Game 2026.730.0 (each has an
/// IStorageResourceProvider-only constructor); there is no bundled "retro" skin in that package.
/// <see cref="Random"/> re-rolls on every song change, and <see cref="Custom"/> means an imported
/// skin — WHICH one is <see cref="JukeBoxSetting.CustomSkinPath"/>'s job, so this enum on its own
/// never fully identifies a skin.
///
/// <para>
/// Deliberately still an enum, and deliberately unchanged, even though the user now picks from a
/// library of imported skins rather than a single Custom slot: the value persists into the ini by
/// MEMBER NAME (see the <see cref="JukeBoxSetting"/> notes on enum persistence) and rides the
/// viewer-sync protocol the same way, so every existing <c>Skin=Custom</c> keeps meaning what it
/// always did. The library is expressed in the second key instead.
/// </para>
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
    /// An imported .osk — the one named by <see cref="JukeBoxSetting.CustomSkinPath"/>. Unlike the
    /// bundled members this is never a dropdown row by itself: the settings list shows one row per
    /// installed skin, each pairing this with its own folder (see <c>SkinChoice</c>), so the
    /// generic description below is only ever a fallback for a folder that has gone missing.
    /// With nothing imported it degrades to <see cref="Argon"/> rather than rendering nothing —
    /// see <c>SkinSelection.CreateEffectiveSkin</c>.
    ///
    /// <para>
    /// Rolled by <see cref="Random"/> alongside the bundled skins: a library the user assembled
    /// themselves is exactly what they want a random skin drawn from, and an entry that fails to
    /// resolve degrades to Argon like any other.
    /// </para>
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
    /// <summary>
    /// The file this manager reads and writes, in app storage — <see cref="IniConfigManager{T}"/>'s
    /// own default, named here so callers can ask whether it exists YET. Its absence is what
    /// JukeBoxGameBase treats as "this app has never run here", which is the only reliable
    /// first-run signal available: the framework's own config file is written out in full the
    /// first time any framework setting moves, which happens during window creation.
    /// </summary>
    public const string CONFIG_FILE = "game.ini";

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

        SetDefault(JukeBoxSetting.ShowStoryboard, true);
        SetDefault(JukeBoxSetting.ShowVideo, true);
        SetDefault(JukeBoxSetting.StoryboardVideoSplitMigrated, false);
        SetDefault(JukeBoxSetting.HiddenStoryboardLayers, string.Empty);

        SetDefault(JukeBoxSetting.RemoveChartMask, false);
        SetDefault(JukeBoxSetting.RemoveStoryboardMask, false);
        SetDefault(JukeBoxSetting.ChartOpacity, 1.0, 0.0, 1.0);

        SetDefault(JukeBoxSetting.ChartMods, string.Empty);
        SetDefault(JukeBoxSetting.HiddenPlayfieldElements, string.Empty);
        SetDefault(JukeBoxSetting.ConvertToRuleset, LazerPlayer.ChartConversionTarget.Off);
        SetDefault(JukeBoxSetting.DiscordRichPresence, true);

        // Both on, because both describe what the app ALREADY does: it fills an empty queue from
        // the radio, and — since MainScreen starts the jukebox unconditionally and the queue is
        // never restored across launches — it has always greeted the user with a random song too.
        // Defaulting either to off would turn existing behaviour into an opt-in, which reads as
        // the upgrade having broken playback rather than as a new setting to reach for.
        SetDefault(JukeBoxSetting.RadioOnEmptyQueue, true);
        SetDefault(JukeBoxSetting.RadioOnStart, true);

        // Every filter defaults to its neutral value, so a fresh install's radio asks exactly the
        // broad question it asked before there were filters at all.
        SetDefault(JukeBoxSetting.RadioMode, RadioRuleset.Any);
        SetDefault(JukeBoxSetting.RadioCategory, osu.Game.Overlays.BeatmapListing.SearchCategory.Ranked);
        SetDefault(JukeBoxSetting.RadioGenre, osu.Game.Overlays.BeatmapListing.SearchGenre.Any);
        SetDefault(JukeBoxSetting.RadioLanguage, osu.Game.Overlays.BeatmapListing.SearchLanguage.Any);
        SetDefault(JukeBoxSetting.RadioHasVideo, false);
        SetDefault(JukeBoxSetting.RadioHasStoryboard, false);
        SetDefault(JukeBoxSetting.RadioMinStars, 0.0, 0.0, 10.0);
        SetDefault(JukeBoxSetting.RadioMaxStars, 10.0, 0.0, 10.0);
        SetDefault(JukeBoxSetting.RadioFeaturedArtists, false);
    }
}
