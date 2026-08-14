#nullable enable

using System.IO;
using osu.Framework.Bindables;
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

    /// <summary>
    /// Mirrors <c>LegacyBeatmapSkin.GetConfig</c>'s exact override for
    /// <see cref="SkinConfiguration.LegacySetting.Version"/>: always returns null, "ignoring
    /// beatmap-level versioning completely". Without this, the .osu-file-as-skin-config parse this
    /// class relies on (see the constructor doc) always reports the legacy decoder's DEFAULT
    /// version (1.0 — beatmaps essentially never declare a real one), never null — and, unlike a
    /// genuinely-versionless lookup, a concrete (if defaulted) value here means the beatmap skin's
    /// own answer wins outright rather than falling through to the user's actual selected skin.
    /// Ruleset code that branches on this (e.g. TaikoLegacySkinTransformer's LegacyHalfDrum, whose
    /// "&gt;=2.1" check picks between two different position calibrations for the drum hit flash)
    /// then always takes the old, sub-2.1 branch for anything resolved through a beatmap skin —
    /// regardless of what the user's actual skin (or the beatmap's own real skin.ini, which this
    /// class doesn't separately parse at all — see the constructor doc, it's the .osu file itself)
    /// would have reported — producing wrong, version-1.0-calibrated positioning even under an
    /// otherwise-modern skin.
    /// </summary>
    public override IBindable<TValue>? GetConfig<TLookup, TValue>(TLookup lookup)
    {
        if (lookup is SkinConfiguration.LegacySetting s && s == SkinConfiguration.LegacySetting.Version)
            return null;

        return base.GetConfig<TLookup, TValue>(lookup);
    }
}
