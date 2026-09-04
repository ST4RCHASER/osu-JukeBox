#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Replays;
using osu.Game.Rulesets.Scoring;
using osuTK;

namespace JukeBox.Game.Replays;

/// <summary>
/// Judges an osu! standard replay WITHOUT a gameplay renderer — danser's approach — by walking the
/// replay's cursor frames against the beatmap's hit objects geometrically. The whole map is judged
/// in one linear pass (a few milliseconds), where the drawable simulator paid the cost of a full
/// <c>DrawableRuleset</c> updated 16ms at a time for the length of the song, which is why 50 replays
/// took two minutes AND why the slider-heavy sections stalled.
///
/// <para>
/// It decides only ONE thing per judged object — was it hit — and the WHEN for the tapped ones.
/// Everything after that is lazer's own: the exact <see cref="HitResult"/> comes from the object's
/// <see cref="HitWindows"/> (tapped) or its <see cref="Judgement"/>'s Max/Min result (tracked), and
/// the score, combo, accuracy and rank come from feeding these into a real
/// <see cref="osu.Game.Rulesets.Osu.Scoring.OsuScoreProcessor"/>. So the NUMBERS are exact lazer
/// numbers; only the hit-detection geometry is re-derived here, and it is cross-checked against the
/// drawable renderer on fixture maps (see TestSceneReplaySimulator's analytic-vs-drawable tests) so
/// a divergence is caught.
/// </para>
/// </summary>
public static class AnalyticOsuJudge
{
    /// <summary>The follow circle covers 2.4x the object radius while a slider is being tracked —
    /// lazer's <c>SliderBall</c> follow area. Ticks and the tail are "hit" only while the cursor is
    /// inside this of the ball AND a key is held.</summary>
    private const float follow_radius_multiplier = 2.4f;

    /// <summary>One judged entity's outcome, in the order the score processor must be fed.</summary>
    public readonly record struct Judged(HitObject Object, HitResult Result, double Time, Vector2 Position);

    /// <summary>Whether this evaluator can judge the given ruleset. osu! standard only — the tracked
    /// geometry (follow circle, cursor) is osu's; other rulesets fall back to the drawable simulator.</summary>
    public static bool Supports(Ruleset ruleset) => ruleset is OsuRuleset;

    /// <summary>
    /// Judges every object in <paramref name="beatmap"/> against <paramref name="frames"/>, returning
    /// the results in the exact order a play produces them (by judgement time, nested slider parts
    /// before the slider that owns them) — the order the score processor's ApplyResult expects.
    /// </summary>
    public static IReadOnlyList<Judged> Evaluate(IBeatmap beatmap, IReadOnlyList<ReplayFrame> frames)
    {
        var cursor = new CursorTrack(frames);
        var results = new List<Judged>();

        // Tappable objects (circles and slider heads) in start-time order, so note-lock — a later
        // object cannot be hit before an earlier one is judged — falls out of processing them in
        // order and consuming presses left to right.
        var topLevel = beatmap.HitObjects.OfType<OsuHitObject>().OrderBy(h => h.StartTime).ToList();

        // The press edges (a key going down that was not down the frame before), in time order, each
        // consumed by at most one object.
        var presses = cursor.PressEdges();
        int pressPointer = 0;

        foreach (var obj in topLevel)
        {
            switch (obj)
            {
                case Spinner spinner:
                    results.Add(judgeSpinner(spinner, cursor));
                    break;

                case Slider slider:
                    judgeSlider(slider, cursor, presses, ref pressPointer, results);
                    break;

                case HitCircle circle:
                    results.Add(judgeCircle(circle, circle, cursor, presses, ref pressPointer));
                    break;
            }
        }

        // Application order is judgement time; a slider's nested parts and the slider aggregate all
        // share nearly the same times, so a stable sort keeps the order they were added in (head,
        // ticks, tail, aggregate) for equal times — which is the order gameplay applies them.
        return results.OrderBy(r => r.Time).ToList();
    }

    /// <summary>
    /// Tap judgement for a circle or a slider head: the earliest still-unused press that lands inside
    /// the object's hit window with the cursor over it. A press inside the window but off the object
    /// is still CONSUMED (note-lock — it cannot fall through to a later object) but leaves this one to
    /// miss. No qualifying press by the time the window closes is a miss.
    /// </summary>
    private static Judged judgeCircle(OsuHitObject judged, OsuHitObject positional, CursorTrack cursor, IReadOnlyList<CursorTrack.Press> presses, ref int pressPointer)
    {
        // The object is hittable within the widest SUCCESSFUL window (the loosest allowed result — a
        // 50's timing is looser than a 300's); past that it auto-misses. Using the far larger "miss
        // window" instead had a missed object swallow the NEXT object's press, cascading the whole
        // rest of the map into misses.
        double hitWindow = widestSuccessfulWindow(judged.HitWindows);
        double windowClose = judged.StartTime + hitWindow;
        float radius = (float)positional.Radius;

        // Skip presses that fall before this object's window — they were offered to earlier objects
        // and either used or wasted; they cannot belong to this one.
        while (pressPointer < presses.Count && presses[pressPointer].Time < judged.StartTime - hitWindow)
            pressPointer++;

        for (int i = pressPointer; i < presses.Count; i++)
        {
            var press = presses[i];

            if (press.Time > windowClose)
                break; // a later object's press — leave it for them

            var result = judged.HitWindows.ResultFor(press.Time - judged.StartTime);

            if (!result.IsHit())
                continue; // too early to register on this object; the press is not yet spent

            // A successful-timing press: it lands the result if the cursor is over the object, and
            // is otherwise spent to nothing (note-lock — it cannot fall through to a later object).
            pressPointer = i + 1;

            if (Vector2.Distance(press.Position, positional.StackedPosition) <= radius)
                return new Judged(judged, result, press.Time, positional.StackedPosition);

            return new Judged(judged, HitResult.Miss, windowClose, positional.StackedPosition);
        }

        return new Judged(judged, HitResult.Miss, windowClose, positional.StackedPosition);
    }

    /// <summary>The loosest timing that still lands a successful result (a 50's window on osu), which
    /// is how long the object stays hittable before it auto-misses.</summary>
    private static double widestSuccessfulWindow(HitWindows windows)
    {
        double widest = 0;

        foreach (var result in new[] { HitResult.Meh, HitResult.Ok, HitResult.Great })
        {
            if (windows.IsHitResultAllowed(result))
                widest = Math.Max(widest, windows.WindowFor(result));
        }

        return widest;
    }

    /// <summary>
    /// A slider: its head is tapped like a circle, then every tick, repeat and its tail is "hit" only
    /// while the player is TRACKING at that moment — a key held with the cursor inside the follow
    /// circle of the ball. The slider's own aggregate result is its Max result when the head landed
    /// (the slider counts for combo), its Min otherwise.
    /// </summary>
    private static void judgeSlider(Slider slider, CursorTrack cursor, IReadOnlyList<CursorTrack.Press> presses, ref int pressPointer, List<Judged> results)
    {
        var nested = slider.NestedHitObjects.OfType<OsuHitObject>().ToList();

        var head = nested.OfType<SliderHeadCircle>().FirstOrDefault();
        bool headHit = false;

        if (head != null)
        {
            var headResult = judgeCircle(head, head, cursor, presses, ref pressPointer);
            headHit = headResult.Result.IsHit();
            results.Add(headResult);
        }

        float followRadius = (float)slider.Radius * follow_radius_multiplier;

        // A slider is COMPLETED when the head was hit and the key stayed held to the tail — the classic
        // slider model: holding to the end completes it, the follow circle being generous. That is what
        // separates a real held/followed slider (key down through the body) from a tap-and-release,
        // which drops the tail exactly as the drawable renderer does. Checked by key-held rather than
        // an instantaneous cursor-on-ball test at the tail, since a real player's cursor is already
        // sliding to the next object at the tail's precise instant.
        var tail = nested.LastOrDefault(p => p.CreateJudgement().MaxResult == HitResult.SmallTickHit);
        bool completed = headHit && tail != null && cursor.KeyHeldAt(tail.StartTime);

        foreach (var part in nested)
        {
            if (part is SliderHeadCircle)
                continue;

            var judgement = part.CreateJudgement();
            bool isTail = judgement.MaxResult == HitResult.SmallTickHit;

            // Ticks and repeats (LargeTick): the strict instantaneous follow-circle check, against the
            // part's own StackedPosition (which lazer computed exactly) — re-deriving the ball position
            // by interpolating the path myself only false-misses ticks on real sliders. The tail rides
            // on slider completion above rather than an exact-instant check.
            bool hit = isTail ? completed : isTracking(part.StackedPosition, part.StartTime, followRadius, cursor);

            var result = hit ? judgement.MaxResult : judgement.MinResult;
            results.Add(new Judged(part, result, part.StartTime, part.StackedPosition));
        }

        // The slider's OWN aggregate:
        //  - COMPLETED → a large tick, carrying combo but NOT accuracy (grading it as the head's Great
        //    instead added a bogus 300 per slider and pinned accuracy at 100%);
        //  - head hit but NOT completed (tapped and released) → IGNORED: no combo, but it does not
        //    break combo either, which is what the drawable renderer records (the tick misses already
        //    broke the combo) — a large-tick MISS here would diverge on that result;
        //  - head missed entirely → an ignored miss.
        var aggregate = completed ? HitResult.LargeTickHit
            : headHit ? HitResult.IgnoreHit
            : HitResult.IgnoreMiss;
        results.Add(new Judged(slider, aggregate, slider.GetEndTime(), slider.StackedPosition));
    }

    /// <summary>Whether the player is tracking the slider ball (at <paramref name="ballPosition"/> at
    /// <paramref name="time"/>): a key held with the cursor inside the follow circle of the ball.</summary>
    private static bool isTracking(Vector2 ballPosition, double time, float followRadius, CursorTrack cursor)
    {
        if (!cursor.KeyHeldAt(time))
            return false;

        return Vector2.Distance(cursor.PositionAt(time), ballPosition) <= followRadius;
    }

    /// <summary>
    /// A spinner: hit or miss by whether the cursor accumulated enough rotation over the spinner's
    /// duration. The exact result letter comes from the spinner's own judgement (Max when the required
    /// spins are reached, Min otherwise); bonus spins are not scored here (they are a small tail on
    /// spinner-heavy maps only).
    /// </summary>
    private static Judged judgeSpinner(Spinner spinner, CursorTrack cursor)
    {
        double rotations = cursor.AccumulatedRotations(spinner.StartTime, spinner.GetEndTime(), new Vector2(256, 192));
        var judgement = spinner.CreateJudgement();
        bool complete = rotations >= spinner.SpinsRequired;
        return new Judged(spinner, complete ? judgement.MaxResult : judgement.MinResult, spinner.GetEndTime(), spinner.StackedPosition);
    }

    /// <summary>
    /// The replay's cursor over time: interpolated position, held keys, and press edges, all read from
    /// the frames by a moving pointer since the queries come in time order.
    /// </summary>
    private sealed class CursorTrack
    {
        public readonly record struct Press(double Time, Vector2 Position);

        private readonly List<OsuReplayFrame> frames;

        public CursorTrack(IReadOnlyList<ReplayFrame> source)
        {
            frames = source.OfType<OsuReplayFrame>().OrderBy(f => f.Time).ToList();
        }

        /// <summary>
        /// Every moment a key goes down — PER KEY. osu! registers each individual button press as a
        /// hit, so in a stream the player holds one key and alternates the other; treating "any key
        /// held" as one continuous hold (an edge only from nothing-held to something-held) sees ONE
        /// press at the start of a stream and none after, which misses almost the whole map. A press
        /// is emitted whenever an action appears that was not held the frame before.
        /// </summary>
        public IReadOnlyList<Press> PressEdges()
        {
            var edges = new List<Press>();
            var previous = new HashSet<OsuAction>();

            foreach (var frame in frames)
            {
                foreach (var action in frame.Actions)
                {
                    if (!previous.Contains(action))
                        edges.Add(new Press(frame.Time, frame.Position));
                }

                previous.Clear();

                foreach (var action in frame.Actions)
                    previous.Add(action);
            }

            return edges;
        }

        /// <summary>Interpolated cursor position at <paramref name="time"/> (linear between frames, as
        /// lazer interpolates replay input).</summary>
        public Vector2 PositionAt(double time)
        {
            if (frames.Count == 0)
                return Vector2.Zero;

            if (time <= frames[0].Time)
                return frames[0].Position;

            if (time >= frames[^1].Time)
                return frames[^1].Position;

            int i = binarySearchBefore(time);
            var a = frames[i];
            var b = frames[i + 1];

            double span = b.Time - a.Time;
            if (span <= 0)
                return a.Position;

            float f = (float)((time - a.Time) / span);
            return a.Position + (b.Position - a.Position) * f;
        }

        /// <summary>Whether any key is held at <paramref name="time"/>.</summary>
        public bool KeyHeldAt(double time)
        {
            if (frames.Count == 0 || time < frames[0].Time)
                return false;

            int i = binarySearchBefore(time);
            return frames[i].Actions.Count > 0;
        }

        /// <summary>Total turns the cursor made around <paramref name="centre"/> between two times —
        /// the spinner rotation count, summing the absolute angle swept.</summary>
        public double AccumulatedRotations(double start, double end, Vector2 centre)
        {
            double radians = 0;
            bool have = false;
            float last = 0;

            foreach (var frame in frames)
            {
                if (frame.Time < start)
                    continue;
                if (frame.Time > end)
                    break;

                float angle = MathF.Atan2(frame.Position.Y - centre.Y, frame.Position.X - centre.X);

                if (have)
                {
                    float delta = angle - last;

                    // Shortest signed step, so a wrap across ±π is not counted as a full turn.
                    while (delta > MathF.PI) delta -= 2 * MathF.PI;
                    while (delta < -MathF.PI) delta += 2 * MathF.PI;

                    radians += Math.Abs(delta);
                }

                last = angle;
                have = true;
            }

            return radians / (2 * Math.PI);
        }

        private int binarySearchBefore(double time)
        {
            int low = 0;
            int high = frames.Count - 1;

            while (low < high)
            {
                int mid = (low + high + 1) / 2;

                if (frames[mid].Time <= time)
                    low = mid;
                else
                    high = mid - 1;
            }

            return low;
        }
    }
}
