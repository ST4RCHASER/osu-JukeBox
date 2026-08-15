#nullable enable

using System.IO;
using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Online;
using JukeBox.Game.UI;
using osu.Framework.Logging;

namespace JukeBox.Game.Import;

/// <summary>
/// Builds the <see cref="BeatmapSetInfo"/> the queue and now-playing UI expect out of a set that
/// was imported locally and therefore never had a mirror search response to parse. Everything
/// comes from the .osu files' own <c>[Metadata]</c> section, which carries the same title / artist
/// / creator the online API would have served.
/// </summary>
public static class LocalBeatmapMetadata
{
    /// <summary>
    /// Describes <paramref name="set"/> from its preferred difficulty's metadata, falling back to
    /// the folder name for a title when a set has no readable metadata at all — a queue row reading
    /// "12345" is still better than a blank one.
    /// </summary>
    public static BeatmapSetInfo Describe(CachedBeatmapSet set)
    {
        OsuFileInfo? info = null;

        if (set.PreferredOsuFile != null)
        {
            try
            {
                info = OsuFileScanner.Scan(set.PreferredOsuFile);
            }
            catch (System.Exception ex)
            {
                Logger.Error(ex, $"LocalBeatmapMetadata: failed to scan '{set.PreferredOsuFile}'");
            }
        }

        string title = info?.Title ?? string.Empty;
        string artist = info?.Artist ?? string.Empty;

        if (title.Length == 0 && string.IsNullOrEmpty(info?.TitleUnicode))
            title = Path.GetFileName(set.Directory);

        return new BeatmapSetInfo
        {
            Id = set.SetId,
            Title = title,
            TitleUnicode = info?.TitleUnicode,
            Artist = artist,
            ArtistUnicode = info?.ArtistUnicode,
            Creator = info?.Creator ?? string.Empty,

            // "local" isn't one of the osu-web statuses; nothing branches on the exact string (it
            // is displayed verbatim on cards), and using it rather than a real status keeps a
            // hand-imported set visibly distinct from a ranked one.
            Status = "local",
            Beatmaps = set.Difficulties.Select(d => new BeatmapInfo
            {
                Mode = RulesetIcons.ModeString(d.Mode),
                Version = d.Version,
            }).ToList(),
        };
    }
}
