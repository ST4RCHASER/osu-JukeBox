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
}

public enum UiLayout
{
    FullscreenOverlay,
    Split,
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
        SetDefault(JukeBoxSetting.UiLayout, UiLayout.FullscreenOverlay);
        SetDefault(JukeBoxSetting.CacheSizeGb, 10.0);
        SetDefault(JukeBoxSetting.ShowFps, false);
    }
}
