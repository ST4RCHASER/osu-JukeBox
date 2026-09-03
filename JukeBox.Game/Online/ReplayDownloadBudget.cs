#nullable enable

using System;
using System.Collections.Generic;

namespace JukeBox.Game.Online;

/// <summary>
/// How many replays we are allowed to fetch, and when.
///
/// <para>
/// osu! throttles <c>/api/v2/scores/{id}/download</c> separately from everything else — ten a
/// minute, against a general allowance of twelve hundred. So the replay downloads, and ONLY the
/// replay downloads, need a budget of their own; the polling requests that decide what to download
/// are nowhere near any limit and are not counted here.
/// </para>
///
/// <para>
/// A sliding window rather than a fixed one: a fixed minute lets ten downloads land in its last
/// second and ten more in the next minute's first, which is twenty requests in two seconds and
/// exactly the burst the throttle exists to stop.
/// </para>
///
/// <para>
/// Time is passed IN rather than read from the clock, so the whole rule is testable at speed and
/// so a caller that already knows "now" (the poller does — it stamps its whole round with one
/// timestamp) cannot disagree with the budget about what time it is.
/// </para>
/// </summary>
public sealed class ReplayDownloadBudget
{
    /// <summary>
    /// Downloads permitted per <see cref="WINDOW"/>. Set to osu!'s actual documented limit rather
    /// than something lower: the cap is what makes four panes feasible at all (see
    /// <see cref="Replays.SpectatePanePlan.MAX_PANES"/>), and spending it is the intended
    /// behaviour. Overshoot is handled by <see cref="Throttled"/> rather than by hoarding.
    /// </summary>
    public const int MAX_PER_WINDOW = 10;

    /// <summary>The window <see cref="MAX_PER_WINDOW"/> is measured over.</summary>
    public static readonly TimeSpan WINDOW = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a 429 stops us for. Longer than <see cref="WINDOW"/> on purpose: being throttled
    /// means our own accounting disagreed with the server's, and the cheapest way to resynchronise
    /// is to let its window drain completely before asking again.
    /// </summary>
    public static readonly TimeSpan THROTTLE_BACKOFF = TimeSpan.FromMinutes(2);

    private readonly Queue<DateTimeOffset> spent = new Queue<DateTimeOffset>();

    private DateTimeOffset blockedUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// Takes one download's worth of budget if there is any, and reports whether it succeeded. A
    /// false answer is an ordinary "not yet", not an error — the caller simply tries the same
    /// player again next round.
    /// </summary>
    public bool TryTake(DateTimeOffset now)
    {
        if (now < blockedUntil)
            return false;

        drain(now);

        if (spent.Count >= MAX_PER_WINDOW)
            return false;

        spent.Enqueue(now);
        return true;
    }

    /// <summary>
    /// Records that osu! answered 429 anyway. Stops all downloads for <see cref="THROTTLE_BACKOFF"/>
    /// and forgets the local tally, which was evidently wrong.
    /// </summary>
    public void Throttled(DateTimeOffset now)
    {
        blockedUntil = now + THROTTLE_BACKOFF;
        spent.Clear();
    }

    /// <summary>How many downloads are available right now — what the UI reports when it explains
    /// why a player is waiting rather than showing.</summary>
    public int Remaining(DateTimeOffset now)
    {
        if (now < blockedUntil)
            return 0;

        drain(now);
        return Math.Max(0, MAX_PER_WINDOW - spent.Count);
    }

    /// <summary>Whether a 429 is still holding us off.</summary>
    public bool IsThrottled(DateTimeOffset now) => now < blockedUntil;

    private void drain(DateTimeOffset now)
    {
        while (spent.Count > 0 && now - spent.Peek() >= WINDOW)
            spent.Dequeue();
    }
}
