#nullable enable

using System;

namespace JukeBox.Game.Online;

/// <summary>
/// What a watched player appears to be doing.
///
/// <para>
/// HONESTY NOTE, because this is the part most likely to be misread as more than it is: osu!'s
/// public API does not expose live activity. There is no "is playing right now", no "paused", no
/// "picking a song" — those exist only on the spectator hub, which is first-party-only (see
/// .superpowers/spectate-research.md). Everything here is INFERRED from the one thing we can see:
/// the player's recently completed scores. So a state is a statement about their last finished
/// play, not about their screen.
/// </para>
/// </summary>
public enum SpectateState
{
    /// <summary>Nothing looked up yet — before the first poll returns.</summary>
    Unknown,

    /// <summary>
    /// A score landed moments ago (within <see cref="SpectateStateRules.FRESH_RESULT_WINDOW"/>) and
    /// is the newest we have seen. REAL, in the sense that a score really did just complete; it is
    /// the closest thing to "they are playing right now" the API can support.
    /// </summary>
    NewResult,

    /// <summary>
    /// They have finished a play recently enough that we are showing it. APPROXIMATED: they may
    /// have stopped since, and we would not know until the window lapses.
    /// </summary>
    Playing,

    /// <summary>
    /// Their most recent play in the window did not pass. REAL — the score itself says so
    /// (<c>passed = false</c>, which is why the poll asks for <c>include_fails=1</c>).
    /// </summary>
    Failed,

    /// <summary>
    /// No completed play inside the window. APPROXIMATED, and deliberately the catch-all: a player
    /// who is paused, sitting in song select, watching a replay, or simply not at their computer is
    /// indistinguishable to us, so all of them read as idle rather than being guessed at.
    /// </summary>
    Idle,

    /// <summary>The username did not resolve to an osu! account.</summary>
    Unknown_User,
}

/// <summary>
/// A watched player's presence, straight from osu!'s user endpoint — and the ONE genuinely live
/// thing the public API gives us about a person.
///
/// <para>
/// Deliberately a separate type from <see cref="SpectateState"/>, because the two are different
/// KINDS of fact and the UI must not blur them. Presence is REAL: osu! says whether the account is
/// online right now. Activity is INFERRED from how recently a score landed. A player can perfectly
/// well be online and idle — that combination is information, not a contradiction, and it is only
/// readable if the two stay apart.
/// </para>
///
/// <para>
/// This is still not the granular activity osu-web's own online list shows ("Clicking circles",
/// "Choosing a beatmap"). That comes from the metadata hub, which is first-party and closed to us
/// on the same terms as the spectator hub. What we have is the dot, not the sentence.
/// </para>
/// </summary>
/// <param name="IsOnline">Whether osu! reports the account online. Always present.</param>
/// <param name="LastVisit">When they were last seen, or null — which is COMMON rather than
/// exceptional, since users can hide it in their privacy settings. Verified live: of three real
/// accounts sampled, one had a timestamp and two had none. So it is worth showing when present and
/// must never be something the presence dot depends on.</param>
public readonly record struct SpectatePresence(bool IsOnline, DateTimeOffset? LastVisit)
{
    /// <summary>Before the first lookup — shown as offline rather than guessed at.</summary>
    public static SpectatePresence Unknown => new SpectatePresence(false, null);
}

/// <summary>
/// Turning "what did this player's last score look like" into a <see cref="SpectateState"/>.
///
/// <para>
/// Pure and static so the mapping can be tested without a network or a clock, and so the one
/// judgement call in the feature — how stale a score may be before the player counts as idle — sits
/// in a single readable place rather than inside the poller's control flow.
/// </para>
/// </summary>
public static class SpectateStateRules
{
    /// <summary>
    /// How recently a score must have finished to read as <see cref="SpectateState.NewResult"/>.
    /// Roughly a beatmap's worth of slack past the ~4s the score feed itself lags by, so a play
    /// that has genuinely just ended is caught while it is still interesting.
    /// </summary>
    public static readonly TimeSpan FRESH_RESULT_WINDOW = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a completed play keeps a player counted as active. Longer than a single map on
    /// purpose: someone between attempts has not stopped playing, and flickering a player to idle
    /// during their song select would make the whole wall twitchy.
    /// </summary>
    public static readonly TimeSpan ACTIVE_WINDOW = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The state for a player whose most recent known score is <paramref name="endedAt"/>.
    /// </summary>
    /// <param name="endedAt">When their newest score finished, or null when they have none.</param>
    /// <param name="passed">Whether that score passed. Ignored when there is no score.</param>
    /// <param name="now">Current time, passed in so the rule is testable without waiting.</param>
    public static SpectateState For(DateTimeOffset? endedAt, bool passed, DateTimeOffset now)
    {
        if (endedAt == null)
            return SpectateState.Idle;

        // osu!'s clock and ours are not the same clock, so this can be slightly NEGATIVE for a
        // score stamped a second or two in the "future". That needs no special case: a negative age
        // is below every window here, so skew lands in NewResult, which is what it should read as.
        // (An explicit clamp to zero lived here until a mutation test showed no behaviour depended
        // on it — dead defensiveness a reader would have had to reason about.)
        var age = now - endedAt.Value;

        if (age > ACTIVE_WINDOW)
            return SpectateState.Idle;

        // Failure outranks freshness: a play that just ended in a fail is more usefully described as
        // failed than as a new result, and it is the one state the API tells us outright.
        if (!passed)
            return SpectateState.Failed;

        return age <= FRESH_RESULT_WINDOW ? SpectateState.NewResult : SpectateState.Playing;
    }

    /// <summary>
    /// The short label the status chip shows. Kept beside the rule rather than in the drawable so
    /// the wording and the meaning cannot drift apart.
    /// </summary>
    public static string Label(SpectateState state) => state switch
    {
        SpectateState.NewResult => "just finished",
        SpectateState.Playing => "playing",
        SpectateState.Failed => "failed",
        SpectateState.Idle => "idle",
        SpectateState.Unknown_User => "unknown player",
        _ => "…",
    };

    /// <summary>
    /// Whether this state means we have something worth RENDERING. Idle and unresolved players keep
    /// their chip but give up their pane, which is what lets a four-pane budget follow whoever is
    /// actually playing.
    ///
    /// <para>
    /// Keyed on ACTIVITY, not presence: being online is not something to render, and a player who
    /// just logged off still has a play worth showing. Presence decides the dot, never the pane.
    /// </para>
    /// </summary>
    public static bool ShouldRender(SpectateState state)
        => state is SpectateState.NewResult or SpectateState.Playing or SpectateState.Failed;

    /// <summary>The dot's label. Real, straight from osu! — see <see cref="SpectatePresence"/>.</summary>
    public static string PresenceLabel(SpectatePresence presence) => presence.IsOnline ? "online" : "offline";

    /// <summary>
    /// How a player's row reads: the real presence and the inferred activity, side by side rather
    /// than merged into one word.
    ///
    /// <para>
    /// Kept as two clauses on purpose. Collapsing them would force a single verdict on facts of
    /// different quality — and would throw away the combinations that carry the most meaning:
    /// "offline · just finished" is someone who played and logged straight off, while
    /// "online · idle" is someone at their computer we simply cannot see into.
    /// </para>
    /// </summary>
    public static string Describe(SpectatePresence presence, SpectateState activity)
        => $"{PresenceLabel(presence)} · {Label(activity)}";
}
