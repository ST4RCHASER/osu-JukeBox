#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace JukeBox.Game.Beatmaps;

public class OsuFileInfo
{
    public string? AudioFilename;
    public int Mode;
    public string? BackgroundFilename;
    public string? VideoFilename;

    /// <summary>[Metadata] Version — the difficulty name (e.g. "Easy", "Insane").</summary>
    public string? Version;

    /// <summary>[Metadata] Title / TitleUnicode / Artist / ArtistUnicode / Creator, i.e. everything
    /// <see cref="Online.BeatmapSetInfo"/> normally gets from a mirror's search response. Read here
    /// so a locally-imported .osz (which never goes near a mirror — see
    /// <see cref="BeatmapCache.ImportArchive"/>) can still present real metadata in the queue and
    /// now-playing UI.</summary>
    public string? Title;

    public string? TitleUnicode;
    public string? Artist;
    public string? ArtistUnicode;
    public string? Creator;

    /// <summary>
    /// [Metadata] BeatmapSetID, or -1 when the file doesn't declare one. Beatmaps exported from the
    /// editor (and anything unsubmitted) legitimately have no id, or carry the sentinel -1 —
    /// callers must treat any non-positive value as "no id" (see
    /// <see cref="BeatmapCache.ResolveArchiveSetId"/>).
    /// </summary>
    public int BeatmapSetId = -1;
}

public class OsuFileScanner
{
    // Reads [General] (AudioFilename, Mode), [Metadata] (Version, title/artist/creator,
    // BeatmapSetID) and [Events] (background "0,0,\"bg.jpg\"" and video "Video,offset,\"v.mp4\"" /
    // "1,offset,..." lines).
    public static OsuFileInfo Scan(string osuPath) => ScanLines(File.ReadLines(osuPath));

    /// <summary>
    /// The line-level half of <see cref="Scan"/>, so a caller holding .osu content that isn't a
    /// file on disk yet can reuse the exact same parse.
    /// </summary>
    public static OsuFileInfo ScanLines(IEnumerable<string> lines)
    {
        var info = new OsuFileInfo();
        string? section = null;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//"))
                continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                if (section == "Events")
                    break; // done scanning Events, stop at the next section

                section = line.Substring(1, line.Length - 2);
                continue;
            }

            switch (section)
            {
                case "General":
                    int colon = line.IndexOf(':');
                    if (colon < 0)
                        break;

                    string key = line.Substring(0, colon).Trim();
                    string value = line.Substring(colon + 1).Trim();

                    switch (key)
                    {
                        case "AudioFilename":
                            info.AudioFilename = value;
                            break;
                        case "Mode":
                            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out info.Mode);
                            break;
                    }
                    break;

                case "Metadata":
                    int metaColon = line.IndexOf(':');
                    if (metaColon < 0)
                        break;

                    string metaKey = line.Substring(0, metaColon).Trim();
                    string metaValue = line.Substring(metaColon + 1).Trim();

                    switch (metaKey)
                    {
                        case "Version":
                            info.Version = metaValue;
                            break;
                        case "Title":
                            info.Title = metaValue;
                            break;
                        case "TitleUnicode":
                            info.TitleUnicode = metaValue;
                            break;
                        case "Artist":
                            info.Artist = metaValue;
                            break;
                        case "ArtistUnicode":
                            info.ArtistUnicode = metaValue;
                            break;
                        case "Creator":
                            info.Creator = metaValue;
                            break;
                        case "BeatmapSetID":
                            int.TryParse(metaValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out info.BeatmapSetId);
                            break;
                    }
                    break;

                case "Events":
                    string[] parts = line.Split(',');
                    if (parts.Length < 3)
                        break;

                    string type = parts[0].Trim();
                    if (type == "0")
                    {
                        info.BackgroundFilename = stripQuotes(parts[2]);
                    }
                    else if (type.Equals("Video", StringComparison.OrdinalIgnoreCase) || type == "1")
                    {
                        info.VideoFilename = stripQuotes(parts[2]);
                    }
                    break;
            }
        }

        return info;
    }

    private static string stripQuotes(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            s = s.Substring(1, s.Length - 2);
        return s;
    }
}
