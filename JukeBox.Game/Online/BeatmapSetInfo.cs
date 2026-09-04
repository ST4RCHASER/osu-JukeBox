#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// osu!'s own API serves genre and language as FLAT ids (<c>genre_id</c>/<c>language_id</c>)
    /// where the mirrors nest them in the <see cref="Genre"/>/<see cref="Language"/> objects. Both
    /// shapes are kept so one DTO deserialises either backend's response; read them through
    /// <see cref="GenreIdOrNull"/>/<see cref="LanguageIdOrNull"/> rather than picking a shape.
    /// </summary>
    public int? GenreId { get; set; }

    /// <summary>See <see cref="GenreId"/>.</summary>
    public int? LanguageId { get; set; }

    public int? GenreIdOrNull => GenreId ?? Genre?.Id;
    public int? LanguageIdOrNull => LanguageId ?? Language?.Id;

    /// <summary>
    /// The official API's ready-made 30-second preview clip (<c>//b.ppy.sh/preview/…</c>), absent
    /// from the mirrors' responses — which is why <see cref="Playback.PreviewPlayer"/> reconstructs
    /// the same URL from the set id instead of relying on this. Kept because it is authoritative
    /// when present.
    /// </summary>
    public string? PreviewUrl { get; set; }

    /// <summary>Whether the set is flagged explicit. Only the official API reports this.</summary>
    public bool Nsfw { get; set; }

    /// <summary>
    /// osu-web's link to the Featured Artist track this set uses, or null for community uploads.
    /// The very field <see cref="SearchFilters.FeaturedArtists"/> filters on, which is what makes it
    /// the only way to CHECK that filter was honoured rather than ignored. Official API only — no
    /// mirror serves it, which is also why that filter can have no client-side stand-in.
    /// </summary>
    public int? TrackId { get; set; }

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
    public Replays.ReplayAttachment? Replay
    {
        get => Replays.Count > 0 ? Replays[0] : null;
        set => Replays = value == null
            ? Array.Empty<Replays.ReplayAttachment>()
            : new[] { value };
    }

    /// <summary>
    /// EVERY replay that resolved to this set — several when a batch of .osr for the same beatmap
    /// arrived together, which is one viewing session rather than several. Empty for a set that
    /// came from an ordinary search or download.
    ///
    /// <para>
    /// <see cref="Replay"/> is the first of these and stays the single-replay view, so everything
    /// that only ever shows one credit is unchanged.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<Replays.ReplayAttachment> Replays { get; set; } = Array.Empty<Replays.ReplayAttachment>();

    /// <summary>
    /// The players of this set for a "Played by …" credit: up to two are listed by name; more than
    /// two collapse to a count ("5 players"), so a big multi-replay set does not spill a paragraph of
    /// names into a one-line credit. Empty for a set with no replays. Does NOT include the "Played by"
    /// prefix — the caller adds it, since the queue card and the playback panel word it slightly
    /// differently.
    /// </summary>
    [JsonIgnore]
    public string PlayerRoster
    {
        get
        {
            var names = Replays.Select(r => r.PlayerName.Length > 0 ? r.PlayerName : "an unknown player").ToList();

            return names.Count switch
            {
                0 => string.Empty,
                1 => names[0],
                2 => $"{names[0]}, {names[1]}",
                _ => $"{names.Count} players",
            };
        }
    }
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
