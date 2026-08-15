#nullable enable

using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Taiko;

namespace JukeBox.Game.UI;

/// <summary>
/// LAZER'S REAL ruleset icons, shared by everything in this app that labels a difficulty by mode —
/// the fullscreen listing's cards (<see cref="FullscreenBeatmapCard"/>) and the now-playing
/// difficulty dropdown (<see cref="DifficultySwitcher"/>), which describe the same difficulty from
/// two different metadata sources and so must not drift apart in how they draw it.
/// </summary>
internal static class RulesetIcons
{
    // One shared instance per ruleset — Ruleset construction isn't free and CreateIcon() is called
    // for every card rebuild and every dropdown item.
    private static readonly Ruleset osu_ruleset = new OsuRuleset();
    private static readonly Ruleset taiko_ruleset = new TaikoRuleset();
    private static readonly Ruleset catch_ruleset = new CatchRuleset();
    private static readonly Ruleset mania_ruleset = new ManiaRuleset();

    /// <summary>
    /// Lazer's icon for one of the online <c>BeatmapInfo.Mode</c> strings (osu/taiko/fruits/mania,
    /// "catch" tolerated), via the matching ruleset's <see cref="Ruleset.CreateIcon"/> — a
    /// <see cref="SpriteIcon"/> over the texture-backed <c>OsuIcon</c> glyphs (see the
    /// <c>OsuIconStore</c> registration in JukeBoxGameBase), not a FontAwesome approximation.
    /// Unknown modes fall back to osu!'s icon, matching how the rest of this app treats
    /// unrecognised modes. Colour is applied by the caller (the glyph textures are white, so a plain
    /// <see cref="Drawable.Colour"/> tint works, same as lazer's own difficulty lists).
    /// </summary>
    public static Drawable Create(string mode) => For(mode).CreateIcon();

    /// <summary>As <see cref="Create(string)"/>, for the <c>[General] Mode</c> integer scanned out
    /// of a local .osu file.</summary>
    public static Drawable Create(int mode) => Create(ModeString(mode));

    public static Ruleset For(string mode) => mode switch
    {
        "taiko" => taiko_ruleset,
        "fruits" or "catch" => catch_ruleset,
        "mania" => mania_ruleset,
        _ => osu_ruleset, // "osu" and anything unknown
    };

    /// <summary>
    /// The online mode string for a local difficulty's <c>[General] Mode</c> integer, so a scanned
    /// .osu file can be matched against the set's online metadata (and iconified) on the same
    /// vocabulary. Every known mode has a native chart renderer; an unknown future mode still plays
    /// (audio/storyboard), just chartless, and lands on osu!'s icon via <see cref="For"/>.
    /// </summary>
    public static string ModeString(int mode) => mode switch
    {
        0 => "osu",
        1 => "taiko",
        2 => "fruits",
        3 => "mania",
        _ => "unknown",
    };
}
