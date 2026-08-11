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
}
