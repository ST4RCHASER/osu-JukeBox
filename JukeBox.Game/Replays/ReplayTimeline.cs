#nullable enable

using System;
using System.Collections.Generic;

namespace JukeBox.Game.Replays;

/// <summary>What a player's scoreboard read at one instant, recorded as it happened.</summary>
/// <param name="Time">Gameplay time of the judgement that produced this state.</param>
/// <param name="Score">Total score after it.</param>
/// <param name="Combo">Combo after it — zero on the judgement that broke it.</param>
/// <param name="Accuracy">Accuracy after it, 0-1.</param>
/// <param name="BrokeCombo">Whether THIS judgement broke a combo the player was holding.</param>
/// <param name="Grade">osu!'s own rank letter for the play so far. Taken from lazer's score
/// processor rather than re-derived from accuracy here, so it says what the game would say.</param>
/// <param name="Performance">Performance points for the play so far.</param>
/// <param name="ComboLost">On a break, the combo that was destroyed — zero otherwise. How big a
/// break was is what decides whether it is worth announcing on the playfield; the combo AFTER a
/// break is always zero and says nothing about how much it cost.</param>
public readonly record struct TimelinePoint(
    double Time,
    long Score,
    int Combo,
    double Accuracy,
    bool BrokeCombo,
    string Grade = "",
    double Performance = 0,
    int ComboLost = 0);

/// <summary>
/// A player's whole play, recorded once as a list of states over time, so that what the scoreboard
/// shows at any moment is a LOOKUP rather than a running total.
///
/// <para>
/// This is the difference between numbers that survive a seek and numbers that do not. A running
/// total is only correct if every judgement has been fed to it exactly once, in order, which stops
/// being true the moment the user scrubs: gameplay hard-seeks past objects that are then never
/// judged, and jumping backwards does not un-judge anything. A recorded timeline has no such
/// state — asking it for 40 seconds in gives the same answer whether the user arrived there by
/// watching, by skipping forward, or by dragging backwards from the end.
/// </para>
///
/// <para>
/// It is also what makes knockout computable. Whether a player is out at a given moment depends on
/// whether they have broken combo YET, which a live scoreboard can answer only by having watched;
/// with the play recorded, the answer is a search.
/// </para>
/// </summary>
public sealed class ReplayTimeline
{
    private readonly List<TimelinePoint> points = new List<TimelinePoint>();

    /// <summary>Every recorded state, in time order.</summary>
    public IReadOnlyList<TimelinePoint> Points => points;

    /// <summary>
    /// How far into the map the simulation has actually got. Points exist only up to here, so a
    /// lookup past it is an extrapolation and says so rather than pretending the play ended.
    /// </summary>
    public double SimulatedTo { get; private set; }

    /// <summary>Whether the whole play has been recorded.</summary>
    public bool Complete { get; private set; }

    /// <summary>The state before a single judgement: no score, no combo, and — matching lazer —
    /// accuracy at 100%, which is what an unjudged play reads.</summary>
    public static readonly TimelinePoint Start = new TimelinePoint(double.NegativeInfinity, 0, 0, 1, false);

    /// <summary>
    /// Records one judgement. Times must not go backwards; a judgement that arrives out of order is
    /// dropped rather than corrupting the search below, which assumes sorted times.
    /// </summary>
    public void Record(TimelinePoint point)
    {
        if (points.Count > 0 && point.Time < points[^1].Time)
            return;

        points.Add(point);
        SimulatedTo = Math.Max(SimulatedTo, point.Time);
    }

    /// <summary>Marks the play fully simulated, with <paramref name="endTime"/> as the map's end.</summary>
    public void MarkComplete(double endTime)
    {
        SimulatedTo = Math.Max(SimulatedTo, endTime);
        Complete = true;
    }

    /// <summary>
    /// The state as of <paramref name="time"/>: the last judgement at or before it, or
    /// <see cref="Start"/> when the play has not begun. Binary search, because this is called for
    /// every player on every frame.
    /// </summary>
    public TimelinePoint At(double time)
    {
        if (points.Count == 0 || time < points[0].Time)
            return Start;

        int low = 0;
        int high = points.Count - 1;

        while (low < high)
        {
            // Upper midpoint: with low = high - 1 a lower one cannot advance and the loop hangs.
            int mid = (low + high + 1) / 2;

            if (points[mid].Time <= time)
                low = mid;
            else
                high = mid - 1;
        }

        return points[low];
    }

    /// <summary>
    /// When this player first broke a combo they were holding, or null if they never did. Breaks at
    /// or before <paramref name="graceEndSeconds"/> do not count — a fumble in the opening seconds
    /// knocking someone out makes for a dull watch, which is why the reference has the same escape.
    /// </summary>
    public double? FirstComboBreak(double graceEndSeconds)
    {
        double graceMs = graceEndSeconds * 1000;

        foreach (var point in points)
        {
            if (point.BrokeCombo && point.Time > graceMs)
                return point.Time;
        }

        return null;
    }

    /// <summary>
    /// When this player first dropped below a perfect judgement, or null if they never did — the
    /// stricter rule, where anything less than the best result ends the run.
    /// </summary>
    public double? FirstImperfection()
    {
        foreach (var point in points)
        {
            if (point.Accuracy < 1)
                return point.Time;
        }

        return null;
    }
}
