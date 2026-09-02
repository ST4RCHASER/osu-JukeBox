#nullable enable

using System;

namespace JukeBox.Game.Online
{
    /// <summary>
    /// The individual filters a search backend may or may not be able to express — one flag per row
    /// of the listing's filter block, so a row can ask "can whatever is about to serve this actually
    /// do it?" rather than the whole block living or dying on one boolean.
    ///
    /// <para>
    /// This exists because the backends are wildly unequal and a backend that cannot express a
    /// filter does not reject it — it silently returns unfiltered results. osu!'s own API takes
    /// everything here; NeriNyan takes everything except genre and language; catboy.best takes a
    /// ruleset, a status and paging; osu.direct takes a keyword and nothing else. Offering a row
    /// the active backend will ignore is a control that lies, so the listing hides it instead.
    /// </para>
    /// </summary>
    [Flags]
    public enum SearchFilters
    {
        None = 0,

        /// <summary>Free-text query. Every backend has this; it is what remains when all else goes.</summary>
        Keyword = 1 << 0,

        /// <summary>Ruleset ("Mode" row).</summary>
        Mode = 1 << 1,

        /// <summary>Ranked status ("Categories" row).</summary>
        Status = 1 << 2,

        /// <summary>Has-video / has-storyboard ("Extra" row).</summary>
        Extra = 1 << 3,

        /// <summary>Star-rating range.</summary>
        Stars = 1 << 4,

        /// <summary>Sort criteria and direction.</summary>
        Sort = 1 << 5,

        Genre = 1 << 6,
        Language = 1 << 7,

        /// <summary>Fetching anything past the first page. Not a row — a backend without it simply
        /// never loads more.</summary>
        Paging = 1 << 8,

        /// <summary>
        /// Restricting to sets whose track comes from osu!'s Featured Artist library (osu-web's
        /// "Featured Artists" item in the General row). Answerable only by osu!'s own API, whose
        /// <c>c=featured_artists</c> filters on the set having a <c>track_id</c> — a relationship
        /// that lives in osu-web's own tables, so no mirror search can express it and none of them
        /// serve the field to filter on locally either.
        /// </summary>
        FeaturedArtists = 1 << 9,

        /// <summary>What osu!'s own API can do: all of it.</summary>
        All = Keyword | Mode | Status | Extra | Stars | Sort | Genre | Language | Paging | FeaturedArtists,

        /// <summary>What the mirrors can do at best (NeriNyan) — genre, language and featured
        /// artists are osu-web concepts no mirror search exposes.</summary>
        AllMirror = Keyword | Mode | Status | Extra | Stars | Sort | Paging,
    }
}
