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

    // Restricts which field NerinyanMirror's legacy search matches Query against (e.g. "setId").
    // Only NerinyanMirror honours this — the fallback mirrors (CatboyMirror/OsuDirectMirror)
    // ignore it and match their own default field(s) instead.
    public string? Option;

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
}
