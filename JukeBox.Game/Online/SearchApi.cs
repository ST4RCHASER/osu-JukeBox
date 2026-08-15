namespace JukeBox.Game.Online
{
    /// <summary>
    /// Which backend answers beatmap SEARCHES. Downloads are unaffected either way — they always
    /// go through <see cref="SwitchableMirror"/>, since the official API serves metadata only.
    /// </summary>
    public enum SearchApi
    {
        /// <summary>
        /// The beatmap mirrors' own search (see <see cref="SwitchableMirror"/>). The default,
        /// because it needs no credentials — at the cost of the mirrors' legacy filter vocabulary
        /// (no genre, no language, and star ranges only on NeriNyan).
        /// </summary>
        Mirror,

        /// <summary>
        /// osu!'s own <c>GET /api/v2/beatmapsets/search</c> (see <see cref="OfficialBeatmapSearch"/>) —
        /// exact parity with the game and the website, but it requires the user's own OAuth client
        /// id/secret (<see cref="Configuration.JukeBoxSetting.OsuClientId"/>). Falls back to
        /// <see cref="Mirror"/> whenever a request fails, so a bad credential never dead-ends the app.
        /// </summary>
        Official,
    }
}
