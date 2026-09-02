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
    public string? PreferredOsuFile;       // first Mode:0 diff, else first diff

    /// <summary>
    /// This set has no music file BY DESIGN — a keysound-only beatmap, whose entire soundtrack is
    /// per-note hitsound samples and storyboard <c>Sample</c> events (see
    /// <see cref="OsuFileScanner.IsVirtualAudioFilename"/>). Such a set is fully playable:
    /// <see cref="Playback.PlaybackController"/> runs it on a silent track sized from the map's
    /// own content, and <see cref="Screens.BeatmapVisuals"/> forces chart hitsounds on so it isn't
    /// silent. <see cref="AudioFile"/> being null WITHOUT this flag is a genuinely broken set.
    /// </summary>
    public bool HasVirtualAudio;

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

    /// <summary>
    /// [Metadata] Creator — who made THIS difficulty, which is not always whoever owns the set: a
    /// guest difficulty names its own mapper. Null when the file doesn't declare one.
    /// </summary>
    public string? Creator;
}
