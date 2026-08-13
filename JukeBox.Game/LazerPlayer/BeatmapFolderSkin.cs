#nullable enable

using System.IO;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osu.Game.IO;
using osu.Game.Skinning;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// The beatmap folder as a legacy skin: beatmap-provided skin textures, custom hitsound/keysound
/// samples, and the beatmap file's own [Colours] section as skin configuration — the standalone
/// equivalent of lazer's realm-backed <see cref="LegacyBeatmapSkin"/>. Shared by the gameplay
/// chart layer (hitsounds, skin elements) and the storyboard layer (storyboard Sample events).
/// </summary>
internal class BeatmapFolderSkin : LegacySkin
{
    /// <param name="configFile">Absolute path of a .osu (or .osb) file inside the beatmap folder;
    /// its directory becomes the skin root and the file itself is parsed as skin configuration
    /// (LegacySkin's parser accepts .osu content — same trick as lazer's LegacyBeatmapSkin).</param>
    /// <param name="resources">Game-level resource provider.</param>
    /// <param name="host">Host for storage access.</param>
    public BeatmapFolderSkin(string configFile, IStorageResourceProvider resources, GameHost host)
        : base(
            new SkinInfo { Name = Path.GetFileName(configFile) },
            resources,
            new StorageBackedResourceStore(new NativeStorage(Path.GetDirectoryName(configFile)!, host)),
            Path.GetFileName(configFile))
    {
        // Known deviation from LegacyBeatmapSkin: its AllowDefaultComboColoursFallback=false
        // is internal to osu.Game, so a beatmap with no [Colours] section uses the classic
        // default combo colours here instead of the active skin's own palette.
    }
}
