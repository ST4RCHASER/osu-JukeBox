#nullable enable

using System.Text.RegularExpressions;

namespace JukeBox.Game.Online;

/// <summary>What a piece of user-entered text in the "add by ID/link" dialog turned out to point
/// at — see <see cref="BeatmapLink.Parse"/>.</summary>
public enum BeatmapLinkKind
{
    /// <summary>Not a beatmapset ID and not a recognised osu.ppy.sh beatmap link.</summary>
    Invalid,

    /// <summary>A beatmapset (the downloadable unit) — <see cref="BeatmapLink.Id"/> is its ID.</summary>
    BeatmapSet,

    /// <summary>A single difficulty inside a set. <see cref="BeatmapLink.Id"/> is the BEATMAP id,
    /// which no mirror in <see cref="IBeatmapMirror"/> can resolve to its set (the only
    /// field-restricted search option NeriNyan exposes is <c>setId</c>), so this is surfaced to
    /// the user as "paste the beatmapset link instead" rather than looked up.</summary>
    Beatmap,
}

/// <summary>
/// Parses what the user typed into the map-ID dialog: either a bare numeric beatmapset ID or an
/// osu.ppy.sh link. Supported link shapes (scheme and <c>www.</c>/<c>old.</c> host prefixes
/// optional, trailing slashes, fragments and query strings ignored):
///
/// <list type="bullet">
/// <item><c>12345</c> — a bare beatmapset ID.</item>
/// <item><c>osu.ppy.sh/beatmapsets/12345</c> — the modern set page.</item>
/// <item><c>osu.ppy.sh/beatmapsets/12345#osu/67890</c> — a set page deep-linked to one
/// difficulty; the SET id is what's used (the difficulty is picked in the player afterwards).</item>
/// <item><c>osu.ppy.sh/s/12345</c> — the legacy set link.</item>
/// <item><c>osu.ppy.sh/b/67890</c> and <c>osu.ppy.sh/beatmaps/67890</c> — a single difficulty.
/// Recognised, but reported as <see cref="BeatmapLinkKind.Beatmap"/>: resolving a beatmap id to
/// its set needs an API this app's mirrors don't offer (see <see cref="BeatmapLinkKind.Beatmap"/>).</item>
/// </list>
///
/// Anything else — empty text, words, negative or zero ids, links to other hosts — is
/// <see cref="BeatmapLinkKind.Invalid"/>.
/// </summary>
public readonly struct BeatmapLink
{
    public readonly BeatmapLinkKind Kind;

    /// <summary>The parsed id; meaningless (0) when <see cref="Kind"/> is
    /// <see cref="BeatmapLinkKind.Invalid"/>.</summary>
    public readonly int Id;

    public BeatmapLink(BeatmapLinkKind kind, int id)
    {
        Kind = kind;
        Id = id;
    }

    public static readonly BeatmapLink Invalid = new BeatmapLink(BeatmapLinkKind.Invalid, 0);

    // Both anchored at a path separator (or the start of the text) so "…/beatmapsets/1" can't be
    // mistaken for a beatmap link and a bare word ending in "b" can't start one. The set pattern
    // is tried first regardless, since "beatmapsets" shares its prefix with "beatmaps".
    private static readonly Regex set_pattern = new Regex(@"(?:^|/)(?:beatmapsets|s)/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex beatmap_pattern = new Regex(@"(?:^|/)(?:beatmaps|b)/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static BeatmapLink Parse(string? text)
    {
        text = text?.Trim();

        if (string.IsNullOrEmpty(text))
            return Invalid;

        // A bare number is the plain "queue by beatmapset ID" case the dialog started life as.
        if (int.TryParse(text, out int bare))
            return bare > 0 ? new BeatmapLink(BeatmapLinkKind.BeatmapSet, bare) : Invalid;

        // Everything else must actually be an osu! link — a path that merely looks osu-shaped on
        // some other host would otherwise silently resolve to an unrelated id.
        if (text.IndexOf("ppy.sh", System.StringComparison.OrdinalIgnoreCase) < 0)
            return Invalid;

        var setMatch = set_pattern.Match(text);
        if (setMatch.Success && int.TryParse(setMatch.Groups[1].Value, out int setId) && setId > 0)
            return new BeatmapLink(BeatmapLinkKind.BeatmapSet, setId);

        var beatmapMatch = beatmap_pattern.Match(text);
        if (beatmapMatch.Success && int.TryParse(beatmapMatch.Groups[1].Value, out int beatmapId) && beatmapId > 0)
            return new BeatmapLink(BeatmapLinkKind.Beatmap, beatmapId);

        return Invalid;
    }
}
