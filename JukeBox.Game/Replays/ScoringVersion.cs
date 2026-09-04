#nullable enable

using System.Collections.Generic;
using System.Linq;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;

namespace JukeBox.Game.Replays;

/// <summary>
/// The scoring lineage a replay was played under — the one fact that decides BOTH the small tag
/// shown beside its mods in the rail and which formula turns its judgements into a score.
/// </summary>
public enum ScoringVersion
{
    /// <summary>An osu!STABLE play (a legacy .osr). Scored as stable ScoreV1 — the number the play
    /// actually earned on stable and that danser reproduces (see <see cref="StableScoreV1"/>).</summary>
    V1,

    /// <summary>A genuine osu!LAZER play carrying the Classic mod. Scored with lazer's own classic
    /// conversion of its standardised score.</summary>
    Classic,

    /// <summary>A genuine osu!LAZER play under standardised (default lazer) scoring.</summary>
    Lazer,

    /// <summary>A play carrying the ScoreV2 mod. Scored with lazer's standardised (ScoreV2) processor.</summary>
    V2,
}

/// <summary>
/// Reading a replay's <see cref="ScoringVersion"/> off its decoded score, and naming it for the UI.
/// </summary>
public static class ScoringVersions
{
    /// <summary>
    /// The lineage of a decoded score.
    ///
    /// <para>
    /// The load-bearing subtlety, and the reason this cannot just look at the mods: lazer's score
    /// decoder attaches <c>CL</c> (Classic) to EVERY legacy .osr to mark "this was played under
    /// stable's rules". So "has CL" alone does not mean lazer-classic — every stable replay has it.
    /// A stable replay is <see cref="ScoringVersion.V1"/> no matter what mods it carries; only a
    /// genuine LAZER replay (one the decoder did NOT flag <see cref="ScoreInfo.IsLegacyScore"/>) that
    /// actually chose CL is <see cref="ScoringVersion.Classic"/>. That is why the legacy-vs-lazer
    /// origin is checked FIRST, and the mods only within the lazer branch.
    /// </para>
    /// </summary>
    public static ScoringVersion Detect(ScoreInfo score) => Detect(score.IsLegacyScore, score.Mods);

    /// <summary>
    /// The lineage from its two raw signals — whether the decoder flagged the score legacy, and the
    /// mods it carried. Split out from <see cref="Detect(ScoreInfo)"/> so the mapping can be pinned
    /// without building a whole <see cref="ScoreInfo"/>.
    /// </summary>
    public static ScoringVersion Detect(bool isLegacyScore, IEnumerable<Mod> mods)
    {
        // A stable .osr: the LegacyScoreDecoder set IsLegacyScore. Always ScoreV1 — the CL it also
        // attached is auto-added noise here, not a signal.
        if (isLegacyScore)
            return ScoringVersion.V1;

        // A genuine lazer replay from here down, where the mods DO mean what they say.
        var list = mods as IReadOnlyCollection<Mod> ?? mods.ToArray();

        if (list.Any(m => m.Acronym == "SV2"))
            return ScoringVersion.V2;

        if (list.Any(m => m is ModClassic))
            return ScoringVersion.Classic;

        return ScoringVersion.Lazer;
    }

    /// <summary>The short tag drawn beside the mods ("V1", "Classic", "Lazer", "V2").</summary>
    public static string Tag(this ScoringVersion version) => version switch
    {
        ScoringVersion.V1 => "V1",
        ScoringVersion.Classic => "Classic",
        ScoringVersion.Lazer => "Lazer",
        ScoringVersion.V2 => "V2",
        _ => string.Empty,
    };

    /// <summary>
    /// Whether this play is scored with the app's stable ScoreV1 sum (<see cref="StableScoreV1"/>)
    /// rather than lazer's own processor total. True for <see cref="ScoringVersion.V1"/> alone — a
    /// stable play — and the switch that keeps the shipping numbers matching danser.
    /// </summary>
    public static bool UsesStableScoreV1(this ScoringVersion version) => version == ScoringVersion.V1;
}
