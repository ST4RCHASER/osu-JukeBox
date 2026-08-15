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
    public double Bpm { get; set; }
    // osu-web schema `play_count`/`favourite_count` — parsed via the snake_case naming policy in
    // ParseList, same as every other property here. Surfaced on the fullscreen listing's
    // hover-expanded cards (FullscreenBeatmapCard's stats row).
    public long PlayCount { get; set; }
    public int FavouriteCount { get; set; }
    public System.DateTimeOffset? RankedDate { get; set; }
    public NamedIdInfo? Genre { get; set; }
    public NamedIdInfo? Language { get; set; }
    public AvailabilityInfo? Availability { get; set; }
    public List<BeatmapInfo> Beatmaps { get; set; } = new();

    /// <summary>
    /// A replay the user dropped onto the window that resolved to this set, or null for every set
    /// that came from an ordinary search/download. Lives on this type — rather than alongside the
    /// queue — because this is the object that actually travels: <see cref="Playback.MusicQueue"/>
    /// holds these, <see cref="Playback.Jukebox.NowPlaying"/> publishes one, and the queue card and
    /// playback panel both read their "Played by X" credit straight off it, with no parallel
    /// bookkeeping to keep in step. Never serialised — no mirror has a field for it.
    /// </summary>
    [JsonIgnore]
    public Replays.ReplayAttachment? Replay { get; set; }
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

/// <summary>
/// The response's nested <c>genre</c>/<c>language</c> objects — both id and name are nullable in
/// the wild (NeriNyan serves <c>{"id":null,"name":null}</c> for sets that never had one assigned).
/// </summary>
public class NamedIdInfo
{
    public int? Id { get; set; }
    public string? Name { get; set; }
}

public class BeatmapInfo
{
    public int Id { get; set; }
    public string Mode { get; set; } = "osu";
    public string Version { get; set; } = "";
    public double DifficultyRating { get; set; }
    public int TotalLength { get; set; }

    /// <summary>
    /// osu-web's <c>checksum</c>: the MD5 of this difficulty's CANONICAL .osu file, i.e. the one
    /// osu! itself hashes and the one a replay records. Deliberately not assumed to match the file
    /// in our cache: mirrors that repack an archive (NeriNyan rewrites .osu files when serving a
    /// no-video download) change those bytes and therefore their MD5, while this value keeps
    /// naming the difficulty a replay means. See the replay import's difficulty resolution.
    /// </summary>
    public string Checksum { get; set; } = "";
}
