#nullable enable

using System.Collections.Generic;

namespace JukeBox.Game.Beatmaps;

public class CachedBeatmapSet
{
    public int SetId;
    public string Directory = "";          // absolute path of extracted folder
    public string? AudioFile;              // absolute path, from [General] AudioFilename of first difficulty
    public string? OsbFile;                // absolute path or null
    public List<string> OsuFiles = new();  // absolute paths
    public string? VideoFile;              // from Video event, if file exists
    public string? BackgroundFile;         // from background event, if file exists
    public bool Widescreen;
    public string? PreferredOsuFile;       // first Mode:0 diff, else first diff

    /// <summary>One entry per .osu file, in <see cref="OsuFiles"/> order. Only Mode 0 (osu!std)
    /// difficulties are chart-renderable; others still play audio/storyboard fine.</summary>
    public List<DifficultyInfo> Difficulties = new();
}

public class DifficultyInfo
{
    public string Path = "";       // absolute path of the .osu file
    public string Version = "";    // [Metadata] Version (difficulty name); filename fallback
    public int Mode;               // [General] Mode: 0 = osu!std
    public string? AudioFilename;  // [General] AudioFilename (relative, as written in the file)
}
