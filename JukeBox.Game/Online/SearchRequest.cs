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
}
