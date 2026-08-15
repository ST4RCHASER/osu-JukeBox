#nullable enable

namespace JukeBox.Game.Online;

public enum SearchExtra
{
    None,
    Storyboard,
    Video,
    VideoAndStoryboard
}

public class SearchRequest
{
    public string Query = "";
    public int Page;                       // 0-indexed
    public int PageSize = 50;
    public string Status = "ranked";
    public string Sort = "ranked_desc";
    public SearchExtra Extra = SearchExtra.None;

    /// <summary>
    /// Restricts which field NerinyanMirror's legacy search matches <see cref="Query"/> against
    /// (e.g. "setId"). The fallback mirrors ignore it and match their own default field(s)
    /// instead — with one exception: <see cref="CHECKSUM_OPTION"/>, which asks a question a
    /// substring search simply cannot answer, and which every mirror therefore either performs
    /// properly or refuses (see <see cref="CatboyMirror"/>).
    /// </summary>
    public string? Option;

    /// <summary>
    /// <see cref="Option"/> value for "match <see cref="Query"/> against the .osu file's MD5" — the
    /// only identity a dropped replay carries for its beatmap (see
    /// <c>DroppedFileImporter</c>). NerinyanMirror serves it through its legacy
    /// <c>option=checksum</c>; OsuDirectMirror resolves it through its own <c>/md5/</c> route;
    /// CatboyMirror has no equivalent and throws, so <see cref="MirrorChain"/> moves on rather
    /// than mistaking "can't answer" for "no such beatmap".
    /// </summary>
    public const string CHECKSUM_OPTION = "checksum";

    // Ruleset filter as NeriNyan's legacy `m` value ("o"/"t"/"c"/"m"); null = any mode.
    // NerinyanMirror-only — the fallback mirrors ignore it (accepted degradation, their search
    // APIs take neither ruleset nor range filters).
    public string? Mode;

    // Star-rating range. When either bound is set, NerinyanMirror routes the whole search through
    // its `?b64=` base64-JSON body (the only transport that supports range filters — the legacy
    // query string can't express them and POST /search is broken). Fallback mirrors ignore ranges.
    public double? MinStars;
    public double? MaxStars;

    public bool HasStarRange => MinStars.HasValue || MaxStars.HasValue;

    // ---- Official-API-only fields (OfficialBeatmapSearch); the mirrors ignore all of these -----

    /// <summary>
    /// osu-web genre id (lazer's <c>SearchGenre</c> values), null = any. Only
    /// <see cref="OfficialBeatmapSearch"/> can express this as a real parameter — on the mirror path
    /// the same choice is applied client-side to already-loaded results instead (see
    /// <see cref="BeatmapSearchEngine.MatchesClientFilters"/>), which is why the mirror listing
    /// doesn't offer the row at all.
    /// </summary>
    public int? GenreId;

    /// <summary>osu-web language id (lazer's <c>SearchLanguage</c> values), null = any. See
    /// <see cref="GenreId"/> for why this is official-only.</summary>
    public int? LanguageId;

    /// <summary>
    /// The official endpoint's opaque next-page marker (its <c>cursor_string</c>), null for the
    /// first page. The official path pages by cursor rather than by <see cref="Page"/>.
    /// </summary>
    public string? Cursor;

    /// <summary>
    /// Whether explicit sets are included. Sent on every official request rather than left to the
    /// server default, which for a user-less (client-credentials) token means "hide".
    /// </summary>
    public bool IncludeNsfw;
}
