#nullable enable

namespace JukeBox.Game.Online
{
    /// <summary>
    /// Which filters the backend about to serve a search can actually express.
    ///
    /// <para>
    /// Its own type because two callers must agree on the answer and there is no shared instance
    /// between them: <see cref="BeatmapSearchEngine"/> publishes it for the listing's rows, and
    /// <see cref="Playback.RadioService"/> needs the same answer for the radio's own filter set —
    /// but the engine is created by the main screen while the radio is built at game startup, so
    /// neither can read the other's. Both call <see cref="For"/> instead, which keeps a filter
    /// meaning the same thing in both places: two copies of this rule would drift, and a radio that
    /// sent a filter the listing hides would hand it to a mirror that silently ignores it.
    /// </para>
    /// </summary>
    public static class SearchCapability
    {
        /// <param name="api">The user's backend preference.</param>
        /// <param name="mirror">The mirror chain, whose own capability is the union of the mirrors
        /// currently considered healthy — so this answer moves with mirror health, not just with
        /// the setting.</param>
        public static SearchFilters For(SearchApi api, IBeatmapMirror mirror)
            // osu!'s own API expresses the entire filter block; the mirrors express whichever
            // subset the reachable ones between them can.
            => api == SearchApi.Official ? SearchFilters.All : mirror.SupportedFilters;
    }
}
