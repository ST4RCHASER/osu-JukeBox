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
    ShowFps,
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
    }
}
