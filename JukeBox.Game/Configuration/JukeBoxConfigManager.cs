using JukeBox.Game.Online;
using osu.Framework.Configuration;
using osu.Framework.Platform;

namespace JukeBox.Game.Configuration;

public enum JukeBoxSetting
{
    Volume,
    NoVideoDownloads,
    UiLayout,
    CacheSizeGb,
    ShowFps,
    PreferredMirror,
    RenderChart,
    PlayHitSounds,
    BackgroundDim,
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
    }
}
