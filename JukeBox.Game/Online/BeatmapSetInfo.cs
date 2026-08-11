#nullable enable

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JukeBox.Game.Online;

public class BeatmapSetInfo
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string? TitleUnicode { get; set; }
    public string Artist { get; set; } = "";
    public string? ArtistUnicode { get; set; }
    public string Creator { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Video { get; set; }
    public bool Storyboard { get; set; }
    public AvailabilityInfo? Availability { get; set; }
    public List<BeatmapInfo> Beatmaps { get; set; } = new();
    // Prefer the romanized Title/Artist: the default font has no CJK (or other non-Latin) glyph
    // coverage, so preferring TitleUnicode/ArtistUnicode drew as "????" tofu boxes whenever a set's
    // metadata was non-Latin. Fall back to the unicode variant only when the romanized one is
    // missing, rather than dropping the metadata entirely.
    public string DisplayTitle => string.IsNullOrEmpty(Title) ? TitleUnicode ?? "" : Title;
    public string DisplayArtist => string.IsNullOrEmpty(Artist) ? ArtistUnicode ?? "" : Artist;
    public bool DownloadDisabled => Availability?.DownloadDisabled == true;

    public static List<BeatmapSetInfo> ParseList(string json)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };
        return JsonSerializer.Deserialize<List<BeatmapSetInfo>>(json, options) ?? new List<BeatmapSetInfo>();
    }
}

public class AvailabilityInfo
{
    public bool DownloadDisabled { get; set; }
}

public class BeatmapInfo
{
    public int Id { get; set; }
    public string Mode { get; set; } = "osu";
    public string Version { get; set; } = "";
    public double DifficultyRating { get; set; }
    public int TotalLength { get; set; }
}
