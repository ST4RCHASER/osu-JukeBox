#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using osuTK;

namespace JukeBox.Game.Charts;

/// <summary>One catcher-plate movement keyframe (arrive at X by Time).</summary>
public readonly record struct PlateKeyframe(double Time, float X);

/// <summary>One catch droplet (from a slider) or banana (from a spinner).</summary>
public readonly record struct CatchDropSpec(double Time, float X);

/// <summary>
/// Pure, drawable-free computations for the mania/taiko/catch chart renderers — unit-testable
/// without any drawable, mirroring the <see cref="ChartComputations"/> split for osu!std.
/// </summary>
public static class ModeChartComputations
{
    // ---- Mania ---------------------------------------------------------------------------------

    /// <summary>Mania key count from CircleSize (which IS the key count in mode 3), clamped sane.</summary>
    public static int ManiaKeyCount(float circleSize) => Math.Clamp((int)Math.Round(circleSize), 1, 18);

    /// <summary>Lane index for a hit object's X in the 512-wide space: floor(x·keys/512), clamped.</summary>
    public static int ManiaLane(float x, int keys) => Math.Clamp((int)Math.Floor(x * keys / 512f), 0, keys - 1);

    /// <summary>
    /// Whether a lane is the "special" centre lane (odd key counts only) — coloured accent, like
    /// osu!mania's default skin.
    /// </summary>
    public static bool IsSpecialLane(int lane, int keys) => keys % 2 == 1 && lane == keys / 2;

    // ---- Taiko ---------------------------------------------------------------------------------

    /// <summary>Kat (blue rim hit) when whistle or clap is set; otherwise don (red centre hit).</summary>
    public static bool IsKat(int hitSound) => (hitSound & (2 | 8)) != 0;

    /// <summary>Big (finisher) note when the finish bit is set.</summary>
    public static bool IsBig(int hitSound) => (hitSound & 4) != 0;

    // ---- Catch ---------------------------------------------------------------------------------

    /// <summary>Hard cap on autoplay plate keyframes — hostile object counts must stay bounded.</summary>
    public const int max_plate_keyframes = 2000;

    /// <summary>Droplets sampled per slider (over its whole duration), capped.</summary>
    public const int max_droplets_per_slider = 32;

    /// <summary>Bananas per spinner, capped.</summary>
    public const int max_bananas_per_spinner = 32;

    /// <summary>
    /// Catcher-plate keyframes for autoplay: arrive at each target's X exactly at its time,
    /// visiting targets in time order (duplicates at the same time collapse to the last).
    /// Simple linear moves between keyframes — visually adequate for a display layer. Bounded by
    /// <see cref="max_plate_keyframes"/>.
    /// </summary>
    public static List<PlateKeyframe> PlateKeyframes(IEnumerable<CatchDropSpec> targets)
    {
        var result = new List<PlateKeyframe>();

        foreach (var target in targets.OrderBy(t => t.Time))
        {
            if (result.Count > 0 && Math.Abs(result[^1].Time - target.Time) < 1e-9)
            {
                result[^1] = new PlateKeyframe(target.Time, target.X);
                continue;
            }

            if (result.Count >= max_plate_keyframes)
                break;

            result.Add(new PlateKeyframe(target.Time, Math.Clamp(target.X, 0, 512)));
        }

        return result;
    }

    /// <summary>
    /// Everything the catcher must catch, in time order: fruits (circles + slider heads/tails at
    /// their path-end X), droplets along slider bodies, bananas across spinner durations.
    /// </summary>
    public static List<CatchDropSpec> CatchTargets(ChartBeatmap beatmap)
    {
        var result = new List<CatchDropSpec>();

        foreach (var obj in beatmap.HitObjects)
        {
            switch (obj.Kind)
            {
                case HitObjectKind.Circle:
                    result.Add(new CatchDropSpec(obj.Time, obj.X));
                    break;

                case HitObjectKind.Slider:
                    result.AddRange(SliderDroplets(obj));
                    break;

                case HitObjectKind.Spinner:
                    result.AddRange(Bananas(obj));
                    break;
            }
        }

        return result.OrderBy(t => t.Time).ToList();
    }

    /// <summary>
    /// Droplets along a slider: sampled evenly in time across all (clamped) spans, X read from the
    /// curve position at that moment (ping-pong per span). Includes the head (t=0) and the final
    /// tail. Capped at <see cref="max_droplets_per_slider"/>.
    /// </summary>
    public static List<CatchDropSpec> SliderDroplets(ChartHitObject slider)
    {
        var result = new List<CatchDropSpec>();

        var path = SliderCurve.Sample(slider.CurveType, slider.ControlPoints, slider.PixelLength);
        if (path.Count == 0 || slider.SpanDuration <= 0)
        {
            result.Add(new CatchDropSpec(slider.Time, slider.X));
            return result;
        }

        var cumulative = ChartComputations.CumulativeLengths(path);
        float total = cumulative[^1];

        int slides = ChartComputations.RenderedSlides(slider);
        double duration = slider.SpanDuration * slides;
        int count = Math.Clamp((int)(duration / 150), 2, max_droplets_per_slider - 1);

        for (int i = 0; i <= count; i++)
        {
            double fraction = (double)i / count;               // fraction of the whole (clamped) duration
            double spanProgress = fraction * slides;           // in span units
            int span = Math.Min((int)spanProgress, slides - 1);
            double within = spanProgress - span;               // 0..1 within this span
            bool forward = span % 2 == 0;

            float distance = (float)(forward ? within : 1 - within) * total;
            var pos = ChartComputations.PositionAtDistance(path, cumulative, distance);

            result.Add(new CatchDropSpec(slider.Time + fraction * duration, pos.X));
        }

        return result;
    }

    /// <summary>
    /// Banana shower: bananas spread across the spinner's duration at deterministic
    /// pseudo-random Xs (stable per spinner — no per-run flicker). Capped.
    /// </summary>
    public static List<CatchDropSpec> Bananas(ChartHitObject spinner)
    {
        var result = new List<CatchDropSpec>();

        double duration = Math.Max(0, spinner.EndTime - spinner.Time);
        int count = Math.Clamp((int)(duration / 120), 1, max_bananas_per_spinner);

        // Deterministic: seeded off the spinner's start time so replays/seeks look identical.
        var rng = new Random((int)spinner.Time);

        for (int i = 0; i < count; i++)
        {
            double fraction = count == 1 ? 0.5 : (double)i / (count - 1);
            result.Add(new CatchDropSpec(spinner.Time + fraction * duration, rng.Next(16, 497)));
        }

        return result;
    }
}
