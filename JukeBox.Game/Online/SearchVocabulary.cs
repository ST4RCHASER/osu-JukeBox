#nullable enable

using osu.Game.Overlays.BeatmapListing;

namespace JukeBox.Game.Online
{
    /// <summary>
    /// Translations between the enums the UI selects filters with (lazer's own beatmap-listing
    /// enums, whose values are osu-web's) and the vocabulary <see cref="SearchRequest"/> carries
    /// (the mirrors' legacy spellings, which <see cref="OfficialBeatmapSearch"/> re-encodes on its
    /// way out).
    ///
    /// <para>
    /// Shared rather than sitting with the listing that first needed it, because the radio's filter
    /// set now makes exactly the same choices and has to mean exactly the same thing by them: a
    /// second copy of "Any is spelled all" is a fork that only shows up as the radio quietly
    /// searching a different status than the row it was set from.
    /// </para>
    /// </summary>
    public static class SearchVocabulary
    {
        /// <summary>Lazer's Categories value as the status <see cref="SearchRequest.Status"/> holds.</summary>
        public static string CategoryToEngine(SearchCategory category) => category switch
        {
            SearchCategory.Any => SearchRequest.ANY_STATUS,
            _ => category.ToString().ToLowerInvariant(),
        };

        /// <summary>The inverse of <see cref="CategoryToEngine"/>. Anything unrecognised reads as
        /// Ranked, which is the engine's own default status.</summary>
        public static SearchCategory CategoryFromEngine(string category) => category switch
        {
            SearchRequest.ANY_STATUS => SearchCategory.Any,
            "leaderboard" => SearchCategory.Leaderboard,
            "qualified" => SearchCategory.Qualified,
            "loved" => SearchCategory.Loved,
            "pending" => SearchCategory.Pending,
            "wip" => SearchCategory.Wip,
            "graveyard" => SearchCategory.Graveyard,
            _ => SearchCategory.Ranked,
        };

        /// <summary>
        /// An osu! ruleset id as the mirrors' single-letter <see cref="SearchRequest.Mode"/>; null
        /// (= any mode) for anything that isn't one of the four rulesets.
        /// </summary>
        public static string? ModeLetter(int rulesetId) => rulesetId switch
        {
            0 => "o",
            1 => "t",
            2 => "c",
            3 => "m",
            _ => null,
        };
    }
}
