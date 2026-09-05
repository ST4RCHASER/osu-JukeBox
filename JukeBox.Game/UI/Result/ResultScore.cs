#nullable enable

using JukeBox.Game.Replays;

namespace JukeBox.Game.UI.Result;

/// <summary>
/// Which number the result screen shows as a player's total score. The rail and the grid read their
/// scores from the recorded <see cref="ReplayTimeline"/> — simulated under the play's own scoring
/// version, stable ScoreV1 for a V1 play — so the result screen must read the SAME source, or the
/// number at the end would not be the number the rail was showing a second earlier. The decoded
/// <c>ScoreInfo.TotalScore</c> is lazer's standardised figure and only stands in when there is no
/// recording at all (a single replay, which drives the chart directly and is never simulated).
/// </summary>
public static class ResultScore
{
    /// <summary>The final score for a player: the last recorded timeline point when there is one,
    /// else <paramref name="decodedTotal"/>.</summary>
    public static long FinalScore(ReplayTimeline? timeline, long decodedTotal)
        => timeline != null && timeline.Points.Count > 0 ? timeline.Points[^1].Score : decodedTotal;
}
