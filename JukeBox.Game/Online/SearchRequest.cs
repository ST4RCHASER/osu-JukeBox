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
}
