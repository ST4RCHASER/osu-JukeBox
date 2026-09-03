#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace JukeBox.Game.Replays;

/// <summary>When, if ever, a player stops being in the running.</summary>
public enum KnockoutMode
{
    /// <summary>
    /// Nobody is ever out — every replay plays to the end and the board just sorts. The DEFAULT,
    /// because it is the only mode that shows you all of the plays you dropped in; the elimination
    /// modes are for when you want a contest instead of a comparison.
    /// </summary>
    Showcase,

    /// <summary>Out on the first combo break after the grace period, the way a Sudden Death run ends.</summary>
    ComboBreak,

    /// <summary>Out on the first judgement that is less than perfect, the way a Perfect run ends.</summary>
    Imperfection,
}

/// <summary>
/// What the board is ordered by — the same three the reference offers.
///
/// <para>
/// Combo used to be the third option here, chosen because there was no pp to offer. The reference
/// has no combo option and does have pp, and pp is the number people actually rank plays by.
/// </para>
/// </summary>
public enum KnockoutSort
{
    Score,
    Performance,
    Accuracy,
}

/// <summary>
/// The rules of a knockout: who is out, when, and how the board is ordered while it happens.
///
/// <para>
/// A plain value with no drawables anywhere near it, because these decisions want testing without
/// a game host — "does this player survive to 40 seconds" should be answerable by arithmetic on a
/// recorded play, not by watching one.
/// </para>
/// </summary>
/// <param name="Mode">When a player is eliminated. Defaults to nobody being eliminated.</param>
/// <param name="GraceEndSeconds">Breaks at or before this point are forgiven. Matches the
/// reference's default of ten seconds, which exists so an early fumble does not end a run before
/// the viewer has worked out who anybody is.</param>
/// <param name="LiveSort">Whether the board re-orders as the plays diverge, rather than holding the
/// order it started in.</param>
/// <param name="SortBy">What the ordering is on.</param>
///
/// <remarks>
/// A record CLASS rather than a struct, and that is load-bearing. A struct's parameterless
/// constructor zero-fills its fields and does not run the primary constructor, so with a struct
/// <c>new KnockoutRules()</c> silently produced GraceEndSeconds 0 and LiveSort FALSE — every
/// default in the signature above quietly ignored, and a board that refused to re-order with no
/// error anywhere. See the defaults test, which exists to pin exactly that.
/// </remarks>
/// <param name="MinimumAlive">Elimination stops once this many players are left, so a knockout
/// always ends with survivors rather than with an empty board. The reference has the same floor.</param>
/// <param name="BubbleMinimumCombo">The smallest combo whose loss is worth announcing on the
/// playfield. Every break firing a name flash is a continuous flicker at high player counts, which
/// makes the cue useless exactly when it should carry the most — the reference gates it at 200.</param>
public sealed record KnockoutRules(
    KnockoutMode Mode = KnockoutMode.Showcase,
    double GraceEndSeconds = 10,
    bool LiveSort = true,
    KnockoutSort SortBy = KnockoutSort.Score,
    int MinimumAlive = 1,
    int BubbleMinimumCombo = 200)
{
    /// <summary>
    /// Whether a break that cost <paramref name="comboLost"/> is worth announcing on the playfield.
    ///
    /// <para>
    /// The flash is meant to say "that mattered". Firing on every dropped combo at 47 players is a
    /// near-continuous flicker of red names, and a cue that never stops is not a cue.
    /// </para>
    /// </summary>
    public bool WorthAnnouncing(int comboLost) => comboLost >= BubbleMinimumCombo;

    /// <summary>
    /// When <paramref name="timeline"/>'s player is knocked out, or null if they last the map.
    /// Null in <see cref="KnockoutMode.Showcase"/> for everyone, by definition.
    /// </summary>
    public double? KnockedOutAt(ReplayTimeline timeline) => Mode switch
    {
        KnockoutMode.ComboBreak => timeline.FirstComboBreak(GraceEndSeconds),
        KnockoutMode.Imperfection => timeline.FirstImperfection(),
        _ => null,
    };

    /// <summary>Whether that player is still in it at <paramref name="time"/>, ignoring the floor.</summary>
    public bool AliveAt(ReplayTimeline timeline, double time)
        => KnockedOutAt(timeline) is not { } out_ || time < out_;

    /// <summary>
    /// Whether the player at <paramref name="index"/> is still in it, WITH the survivor floor
    /// applied — the answer the board should use.
    ///
    /// <para>
    /// The floor is a property of the whole field, not of one play, which is why this needs all of
    /// them. Without it a knockout can eliminate everybody and end on an empty board, which is a
    /// worse ending than one survivor: eliminations stop once the field is down to
    /// <see cref="MinimumAlive"/>, and whoever was still standing at that moment stays standing.
    /// </para>
    /// </summary>
    public bool AliveAt(IReadOnlyList<ReplayTimeline> timelines, int index, double time)
        => !Eliminated(timelines, time).Contains(index);

    /// <summary>
    /// Who is actually out at <paramref name="time"/>. Players are eliminated in the order they
    /// fell, and the last <see cref="MinimumAlive"/> of them are spared however badly they played.
    /// </summary>
    public IReadOnlyCollection<int> Eliminated(IReadOnlyList<ReplayTimeline> timelines, double time)
    {
        var rules = this;

        var fallen = Enumerable.Range(0, timelines.Count)
                               .Select(i => (index: i, at: rules.KnockedOutAt(timelines[i])))
                               .Where(p => p.at is { } t && time >= t)
                               .OrderBy(p => p.at!.Value)
                               .ThenBy(p => p.index)
                               .ToList();

        int allowed = Math.Max(0, timelines.Count - Math.Max(MinimumAlive, 0));

        return fallen.Take(Math.Min(fallen.Count, allowed)).Select(p => p.index).ToHashSet();
    }

    /// <summary>
    /// The board at <paramref name="time"/>: everyone still alive in order, then everyone already
    /// out in the order they went, most recent first.
    ///
    /// <para>
    /// The eliminated keep their place on the board rather than vanishing, because a knockout the
    /// viewer cannot see happen is just a name disappearing. Ordering the dead by when they died
    /// means the most recent casualty sits closest to the survivors, which is where the eye
    /// already is.
    /// </para>
    /// </summary>
    public IReadOnlyList<int> Standings(IReadOnlyList<ReplayTimeline> timelines, double time)
    {
        var rules = this;
        var order = Enumerable.Range(0, timelines.Count).ToList();

        // Held order: whatever the plays were ranked by before a single note was hit, which for
        // recorded replays is their final result. Sorting live is the interesting case; this is the
        // escape for someone who finds a re-ordering board distracting.
        double sortAt = LiveSort ? time : double.NegativeInfinity;

        // Computed ONCE for the whole sort rather than per comparison: it depends on the entire
        // field, and recomputing it inside a comparator is both wasteful and a good way to give the
        // sort an inconsistent view of who is out.
        var eliminated = Eliminated(timelines, time);

        order.Sort((a, b) =>
        {
            bool aliveA = !eliminated.Contains(a);
            bool aliveB = !eliminated.Contains(b);

            if (aliveA != aliveB)
                return aliveA ? -1 : 1;

            if (!aliveA)
            {
                // Both out: the later death ranks higher, since they survived longer.
                double outA = rules.KnockedOutAt(timelines[a]) ?? 0;
                double outB = rules.KnockedOutAt(timelines[b]) ?? 0;

                if (Math.Abs(outA - outB) > double.Epsilon)
                    return outB.CompareTo(outA);
            }

            int byValue = rules.ValueOf(timelines[b], sortAt).CompareTo(rules.ValueOf(timelines[a], sortAt));

            // Ties resolved by drop order, so a board of identical plays does not shuffle every
            // frame on the whim of an unstable sort.
            return byValue != 0 ? byValue : a.CompareTo(b);
        });

        return order;
    }

    /// <summary>The number this player is ranked on at <paramref name="time"/>.</summary>
    public double ValueOf(ReplayTimeline timeline, double time)
    {
        var point = timeline.At(time);

        return SortBy switch
        {
            KnockoutSort.Accuracy => point.Accuracy,
            KnockoutSort.Performance => point.Performance,
            _ => point.Score,
        };
    }

    /// <summary>How many players are still in it at <paramref name="time"/>, floor included.</summary>
    public int AliveCount(IReadOnlyList<ReplayTimeline> timelines, double time)
        => timelines.Count - Eliminated(timelines, time).Count;

    /// <summary>
    /// How big a cursor should be drawn, growing as the field thins: with everyone still in, every
    /// cursor is small enough that the playfield is not a mess of pointers; once it is down to one,
    /// that cursor is the thing you are watching. Interpolated linearly on the count.
    /// </summary>
    /// <param name="alive">Players still in it.</param>
    /// <param name="total">Players who started.</param>
    /// <param name="min">Size when everyone is alive.</param>
    /// <param name="max">Size when one is left.</param>
    public static float CursorScale(int alive, int total, float min = 1, float max = 2.2f)
    {
        if (total <= 1)
            return max;

        // alive can exceed total only if called with mismatched inputs; clamping keeps the
        // interpolation inside the stated range rather than extrapolating a giant cursor.
        int clamped = Math.Clamp(alive, 1, total);
        float thinned = (float)(total - clamped) / (total - 1);

        return min + (max - min) * thinned;
    }
}
