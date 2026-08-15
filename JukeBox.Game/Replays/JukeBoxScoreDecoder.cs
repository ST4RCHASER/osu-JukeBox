#nullable enable

using System.IO;
using JukeBox.Game.LazerPlayer;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;

namespace JukeBox.Game.Replays;

/// <summary>
/// lazer's real <see cref="LegacyScoreDecoder"/>, wired to this app's (realm-free) world: its two
/// abstract hooks are the ruleset lookup and the beatmap lookup, both of which lazer normally
/// serves out of realm.
///
/// <para>
/// The beatmap hook ignores the MD5 it is handed and always returns the ONE difficulty this
/// decoder was constructed for — the caller has already resolved that difficulty by matching the
/// replay's checksum against the cached set's .osu files (see
/// <see cref="Import.DroppedFileImporter"/>), so there is no second lookup to do, and a decoder
/// that could only ever answer for one beatmap is exactly the shape of a one-shot import.
/// </para>
/// </summary>
public class JukeBoxScoreDecoder : LegacyScoreDecoder
{
    private readonly string osuFile;

    /// <param name="osuFile">Absolute path of the .osu the replay was played on.</param>
    public JukeBoxScoreDecoder(string osuFile)
    {
        this.osuFile = osuFile;
    }

    /// <summary>Decodes <paramref name="replayPath"/> into a score with replay frames attached.</summary>
    public Score Decode(string replayPath)
    {
        using var stream = File.OpenRead(replayPath);
        return Parse(stream);
    }

    protected override Ruleset GetRuleset(int rulesetId) => LazerChartLayer.CreateRuleset(rulesetId);

    protected override WorkingBeatmap GetBeatmap(string md5Hash) => new FlatWorkingBeatmap(osuFile);
}
