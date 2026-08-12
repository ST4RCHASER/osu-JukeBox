#nullable enable

using System;
using System.Collections.Generic;
using osuTK;

namespace JukeBox.Game.Charts;

/// <summary>One spline-travel keyframe for the slider ball.</summary>
public readonly record struct BallKeyframe(double Time, Vector2 Position);

/// <summary>One reverse arrow: where/when it sits and when it becomes visible / pops.</summary>
public readonly record struct ReverseArrowSpec(double Time, double AppearTime, Vector2 Position, float RotationDegrees);

/// <summary>One slider tick occurrence (per span pass).</summary>
public readonly record struct SliderTickSpec(double Time, double AppearTime, Vector2 Position);

/// <summary>One follow-point dot between two consecutive same-combo objects.</summary>
public readonly record struct FollowPointSpec(double AppearTime, double DisappearTime, Vector2 Position, float RotationDegrees);

/// <summary>Per-object combo assignment: which combo colour and which number to display.</summary>
public readonly record struct ComboAssignment(int ColourIndex, int NumberInCombo);

/// <summary>
/// Pure, drawable-free chart computations (combo numbering, osu!'s stack-shift algorithm,
/// slider ball keyframes, reverse arrows, slider ticks, follow points) so each can be unit-tested
/// headlessly and the drawables stay thin consumers of pre-computed specs.
/// </summary>
public static class ChartComputations
{
    /// <summary>Objects closer than this (osu-px) are stack candidates — osu!'s STACK_DISTANCE.</summary>
    public const float stack_distance = 3;

    /// <summary>Per-layer stack shift in osu-px at CS-radius 64 (the stable/lazer constant).</summary>
    public const float stack_offset_per_layer = -6.4f;

    /// <summary>Base object radius the stack offset constant is normalized to.</summary>
    public const float base_radius = 64;

    /// <summary>Follow-point dot spacing along the connecting line, in osu-px.</summary>
    public const float follow_point_spacing = 32;

    // ---- Combo assignment ----------------------------------------------------------------------

    /// <summary>
    /// Assigns each object its combo colour index (advanced on every new combo, cycling
    /// <paramref name="colourCount"/>) and its display number within the combo (1-based; resets on
    /// new combo). Spinners neither advance the colour nor consume/display a number, matching
    /// stable's default-skin behaviour closely enough for display.
    /// </summary>
    public static ComboAssignment[] AssignCombos(IReadOnlyList<ChartHitObject> objects, int colourCount)
    {
        var result = new ComboAssignment[objects.Count];

        int colour = 0;
        int number = 0;
        bool anyComboStarted = false;

        for (int i = 0; i < objects.Count; i++)
        {
            var obj = objects[i];

            if (obj.Kind == HitObjectKind.Spinner)
            {
                result[i] = new ComboAssignment(colour, 0);
                continue;
            }

            if (!anyComboStarted || obj.NewCombo)
            {
                if (anyComboStarted)
                    colour = (colour + 1) % Math.Max(1, colourCount);

                number = 0;
                anyComboStarted = true;
            }

            result[i] = new ComboAssignment(colour, ++number);
        }

        return result;
    }

    // ---- Stacking ------------------------------------------------------------------------------

    /// <summary>Drawn-position shift for an object's stack layer, scaled by circle radius.</summary>
    public static Vector2 StackOffset(ChartHitObject obj, float radius)
        => new Vector2(obj.StackHeight * stack_offset_per_layer * (radius / base_radius));

    /// <summary>An object's drawn start position including its stack shift.</summary>
    public static Vector2 StackedPosition(ChartHitObject obj, float radius)
        => new Vector2(obj.X, obj.Y) + StackOffset(obj, radius);

    /// <summary>
    /// The position where an object "ends": circles/spinners in place, sliders at the resting end
    /// of the last span (head again for an even number of slides).
    /// </summary>
    public static Vector2 EndPosition(ChartHitObject obj)
    {
        if (obj.Kind != HitObjectKind.Slider)
            return new Vector2(obj.X, obj.Y);

        if (obj.Slides % 2 == 0)
            return new Vector2(obj.X, obj.Y);

        var path = SliderCurve.Sample(obj.CurveType, obj.ControlPoints, obj.PixelLength);
        return path.Count > 0 ? path[^1] : new Vector2(obj.X, obj.Y);
    }

    /// <summary>
    /// osu!'s classic stack-shift pass (the stable algorithm, as mirrored by lazer's
    /// OsuBeatmapProcessor.ApplyStacking): walks objects back-to-front, chaining overlapping
    /// starts/ends within <c>preempt · StackLeniency</c> into stacks; each earlier member gets a
    /// higher <see cref="ChartHitObject.StackHeight"/>. Objects landing on a slider END get
    /// negative heights (they stack downward from the slider), like stable.
    /// </summary>
    public static void ApplyStacking(ChartBeatmap beatmap)
    {
        if (beatmap.StackingApplied)
            return;

        beatmap.StackingApplied = true;

        var objects = beatmap.HitObjects;
        double stackThreshold = beatmap.PreemptMs * beatmap.StackLeniency;

        for (int i = objects.Count - 1; i > 0; i--)
        {
            int n = i;
            var objectI = objects[i];

            if (objectI.StackHeight != 0 || objectI.Kind == HitObjectKind.Spinner)
                continue;

            if (objectI.Kind == HitObjectKind.Circle)
            {
                while (--n >= 0)
                {
                    var objectN = objects[n];

                    if (objectN.Kind == HitObjectKind.Spinner)
                        continue;

                    if (objectI.Time - objectN.EndTime > stackThreshold)
                        break;

                    if (objectN.Kind == HitObjectKind.Slider &&
                        Vector2.Distance(EndPosition(objectN), new Vector2(objectI.X, objectI.Y)) < stack_distance)
                    {
                        // Circle(s) sitting on a slider's end: push everything from just after the
                        // slider up to (and including) objectI DOWN the stack instead.
                        int offset = objectI.StackHeight - objectN.StackHeight + 1;

                        for (int j = n + 1; j <= i; j++)
                        {
                            var objectJ = objects[j];
                            if (Vector2.Distance(EndPosition(objectN), new Vector2(objectJ.X, objectJ.Y)) < stack_distance)
                                objectJ.StackHeight -= offset;
                        }

                        break;
                    }

                    if (Vector2.Distance(new Vector2(objectN.X, objectN.Y), new Vector2(objectI.X, objectI.Y)) < stack_distance)
                    {
                        objectN.StackHeight = objectI.StackHeight + 1;
                        objectI = objectN;
                    }
                }
            }
            else if (objectI.Kind == HitObjectKind.Slider)
            {
                while (--n >= 0)
                {
                    var objectN = objects[n];

                    if (objectN.Kind == HitObjectKind.Spinner)
                        continue;

                    if (objectI.Time - objectN.Time > stackThreshold)
                        break;

                    if (Vector2.Distance(EndPosition(objectN), new Vector2(objectI.X, objectI.Y)) < stack_distance)
                    {
                        objectN.StackHeight = objectI.StackHeight + 1;
                        objectI = objectN;
                    }
                }
            }
        }
    }

    // ---- Arc-length path helpers ---------------------------------------------------------------

    /// <summary>Cumulative arc lengths for a polyline (index-aligned, [0] = 0).</summary>
    public static float[] CumulativeLengths(IReadOnlyList<Vector2> path)
    {
        var lengths = new float[path.Count];

        for (int i = 1; i < path.Count; i++)
            lengths[i] = lengths[i - 1] + (path[i] - path[i - 1]).Length;

        return lengths;
    }

    /// <summary>Point at <paramref name="distance"/> along the polyline's arc length (clamped).</summary>
    public static Vector2 PositionAtDistance(IReadOnlyList<Vector2> path, float[] cumulative, float distance)
    {
        if (path.Count == 0)
            return Vector2.Zero;
        if (path.Count == 1 || distance <= 0)
            return path[0];

        float total = cumulative[^1];
        if (distance >= total)
            return path[^1];

        int hi = Array.BinarySearch(cumulative, distance);
        if (hi < 0)
            hi = ~hi;
        hi = Math.Clamp(hi, 1, path.Count - 1);

        float segStart = cumulative[hi - 1];
        float segLength = cumulative[hi] - segStart;
        float t = segLength > 0 ? (distance - segStart) / segLength : 0;

        return Vector2.Lerp(path[hi - 1], path[hi], t);
    }

    /// <summary>Forward tangent direction (degrees) at <paramref name="distance"/> along the polyline.</summary>
    public static float TangentAtDistance(IReadOnlyList<Vector2> path, float[] cumulative, float distance)
    {
        if (path.Count < 2)
            return 0;

        var a = PositionAtDistance(path, cumulative, distance - 1);
        var b = PositionAtDistance(path, cumulative, distance + 1);
        var d = b - a;

        if (d.LengthSquared < 1e-12f)
            d = path[^1] - path[0];

        return MathHelper.RadiansToDegrees((float)Math.Atan2(d.Y, d.X));
    }

    // ---- Slider ball ---------------------------------------------------------------------------

    /// <summary>
    /// Piecewise travel keyframes for the slider ball over ALL spans (ping-pong per repeat), in
    /// the same coordinate space as <paramref name="path"/>. Times are strictly increasing; the
    /// per-span sample count adapts so pathological repeat counts can't explode the transform
    /// count. Consumed as chained MoveTo transforms — still zero per-frame evaluation.
    /// </summary>
    public static List<BallKeyframe> BallKeyframes(ChartHitObject slider, IReadOnlyList<Vector2> path, int samplesPerSpan = 28)
    {
        var result = new List<BallKeyframe>();

        if (path.Count == 0 || slider.SpanDuration <= 0)
            return result;

        int slides = Math.Max(1, slider.Slides);
        int samples = Math.Clamp(600 / slides, 4, Math.Max(4, samplesPerSpan));

        var cumulative = CumulativeLengths(path);
        float total = cumulative[^1];

        result.Add(new BallKeyframe(slider.Time, path[0]));

        for (int span = 0; span < slides; span++)
        {
            bool forward = span % 2 == 0;
            double spanStart = slider.Time + span * slider.SpanDuration;

            for (int j = 1; j <= samples; j++)
            {
                double frac = (double)j / samples;
                float distance = (float)(forward ? frac : 1 - frac) * total;

                result.Add(new BallKeyframe(
                    spanStart + frac * slider.SpanDuration,
                    PositionAtDistance(path, cumulative, distance)));
            }
        }

        return result;
    }

    // ---- Reverse arrows ------------------------------------------------------------------------

    /// <summary>
    /// One arrow per repeat boundary (slides − 1 total): sits on the end the ball is about to
    /// bounce off, pointing along the direction of the NEXT span's travel, visible during the
    /// preceding span (the first one from the slider's fade-in, governed by
    /// <paramref name="preempt"/>), popping at its repeat time.
    /// </summary>
    public static List<ReverseArrowSpec> ReverseArrows(ChartHitObject slider, IReadOnlyList<Vector2> path, double preempt)
    {
        var result = new List<ReverseArrowSpec>();

        if (path.Count < 2 || slider.Slides < 2)
            return result;

        var cumulative = CumulativeLengths(path);
        float total = cumulative[^1];

        for (int r = 1; r < slider.Slides; r++)
        {
            bool atTail = r % 2 == 1;

            var position = atTail ? path[^1] : path[0];

            // Next span travels backward from the tail / forward from the head.
            float rotation = atTail
                ? TangentAtDistance(path, cumulative, total) + 180
                : TangentAtDistance(path, cumulative, 0);

            double time = slider.Time + r * slider.SpanDuration;
            double appear = r == 1 ? slider.Time - preempt : slider.Time + (r - 1) * slider.SpanDuration;

            result.Add(new ReverseArrowSpec(time, appear, position, rotation));
        }

        return result;
    }

    // ---- Slider ticks --------------------------------------------------------------------------

    /// <summary>Ticks closer than this (osu-px, along the path) to either end are dropped.</summary>
    public const float tick_end_margin = 10;

    private const int max_ticks_per_span = 32;
    private const int max_ticks_per_slider = 64;

    /// <summary>
    /// Tick occurrences for every span: spaced <c>beatLength / SliderTickRate</c> in time within a
    /// span, positioned along the body (skipping within <see cref="tick_end_margin"/> of the
    /// ends), passed in reverse order on backward spans. First-span ticks appear with the body
    /// (<paramref name="preempt"/> before the head); later spans' at their span start. Capped so
    /// hostile inputs can't explode the drawable count.
    /// </summary>
    public static List<SliderTickSpec> SliderTicks(ChartHitObject slider, ChartBeatmap beatmap, IReadOnlyList<Vector2> path, double preempt)
    {
        var result = new List<SliderTickSpec>();

        if (path.Count < 2 || slider.SpanDuration <= 0)
            return result;

        double tickInterval = beatmap.BeatLengthAt(slider.Time) / Math.Max(0.1, beatmap.SliderTickRate);

        if (tickInterval <= 0 || !double.IsFinite(tickInterval))
            return result;

        var cumulative = CumulativeLengths(path);
        float total = cumulative[^1];

        // Tick positions within one forward span (time offset from span start → path distance).
        var spanTicks = new List<(double Offset, Vector2 Position)>();

        for (int k = 1; k * tickInterval < slider.SpanDuration - 1e-6 && k <= max_ticks_per_span; k++)
        {
            double offset = k * tickInterval;
            float distance = (float)(offset / slider.SpanDuration) * total;

            if (distance <= tick_end_margin || total - distance <= tick_end_margin)
                continue;

            spanTicks.Add((offset, PositionAtDistance(path, cumulative, distance)));
        }

        int slides = Math.Max(1, slider.Slides);

        for (int span = 0; span < slides && result.Count < max_ticks_per_slider; span++)
        {
            bool forward = span % 2 == 0;
            double spanStart = slider.Time + span * slider.SpanDuration;
            double appear = span == 0 ? slider.Time - preempt : spanStart;

            foreach (var (offset, position) in spanTicks)
            {
                if (result.Count >= max_ticks_per_slider)
                    break;

                double time = spanStart + (forward ? offset : slider.SpanDuration - offset);
                result.Add(new SliderTickSpec(time, appear, position));
            }
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }

    // ---- Follow points -------------------------------------------------------------------------

    /// <summary>How long before its progressive reveal moment a follow-point dot fades in.</summary>
    public const double follow_point_fade_lead = 450;

    /// <summary>
    /// Faint dot trails between consecutive objects within the same combo (never into/out of
    /// spinners or across a new combo): dots every <see cref="follow_point_spacing"/> osu-px along
    /// the line from A's end (stack-shifted) to B's start, revealed progressively — each dot's
    /// window is centred on its fraction of the A→B time gap — and all gone by B's hit time.
    /// </summary>
    public static List<FollowPointSpec> FollowPoints(ChartBeatmap beatmap, float radius)
    {
        var result = new List<FollowPointSpec>();
        var objects = beatmap.HitObjects;

        for (int i = 1; i < objects.Count; i++)
        {
            var a = objects[i - 1];
            var b = objects[i];

            if (b.NewCombo || a.Kind == HitObjectKind.Spinner || b.Kind == HitObjectKind.Spinner)
                continue;

            var start = EndPosition(a) + StackOffset(a, radius);
            var end = StackedPosition(b, radius);

            var delta = end - start;
            float length = delta.Length;

            // Not enough room for even one dot outside the padded ends.
            float padding = radius * 1.5f;
            if (length < padding * 2 + follow_point_spacing)
                continue;

            var direction = delta / length;
            float rotation = MathHelper.RadiansToDegrees((float)Math.Atan2(delta.Y, delta.X));

            for (float d = padding + follow_point_spacing / 2; d <= length - padding; d += follow_point_spacing)
            {
                double fraction = d / length;
                double reveal = a.EndTime + fraction * (b.Time - a.EndTime);

                result.Add(new FollowPointSpec(
                    reveal - follow_point_fade_lead,
                    b.Time,
                    start + direction * d,
                    rotation));
            }
        }

        return result;
    }
}
