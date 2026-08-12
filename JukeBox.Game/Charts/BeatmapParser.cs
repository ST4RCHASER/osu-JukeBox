#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using osu.Framework.Logging;
using osuTK;

namespace JukeBox.Game.Charts;

/// <summary>
/// One timing point from the [TimingPoints] section. Uninherited points carry a positive
/// <see cref="BeatLength"/> (ms per beat); inherited points carry a negative raw beatLength that
/// encodes a slider-velocity multiplier (<c>-100 / beatLength</c>), pre-computed into
/// <see cref="SvMultiplier"/> here.
/// </summary>
public class ChartTimingPoint
{
    public double Time;
    public double BeatLength;
    public bool Uninherited;
    public double SvMultiplier = 1;
    public int SampleSet;   // 0 = default, 1 = normal, 2 = soft, 3 = drum
    public int SampleIndex;
    public int Volume = 100;
}

public enum HitObjectKind
{
    Circle,
    Slider,
    Spinner,
}

public class ChartHitObject
{
    public float X;
    public float Y;

    /// <summary>Stack layer assigned by <see cref="ChartComputations.ApplyStacking"/>: 0 = not
    /// stacked; each layer shifts the drawn position by −6.4 osu-px (radius-scaled) on both axes.</summary>
    public int StackHeight;
    public double Time;
    public HitObjectKind Kind;
    public int HitSound;
    public bool NewCombo;

    /// <summary>Equal to <see cref="Time"/> for circles; slider tail / spinner end otherwise.</summary>
    public double EndTime;

    // ---- Slider-only fields --------------------------------------------------------------------

    public char CurveType = 'B';

    /// <summary>Control points including the head (X, Y) as the first point.</summary>
    public List<Vector2> ControlPoints = new();

    public int Slides = 1;
    public double PixelLength;

    /// <summary>Duration of ONE slider span (head → tail); total duration is this × <see cref="Slides"/>.</summary>
    public double SpanDuration;

    /// <summary>Per-edge hitSound bitmasks (head, each repeat, tail), when the map specifies them.</summary>
    public int[]? EdgeSounds;
}

/// <summary>
/// The subset of a .osu file needed to render a gameplay chart and play its hitsounds:
/// [Difficulty] values, [TimingPoints] and [HitObjects]. Produced by <see cref="BeatmapParser"/>.
/// </summary>
public class ChartBeatmap
{
    public float CircleSize = 5;
    public float ApproachRate = 5;
    public double SliderMultiplier = 1.4;
    public double SliderTickRate = 1;

    /// <summary>[General] StackLeniency — scales the time window within which overlapping
    /// objects are considered a stack (osu! default 0.7).</summary>
    public float StackLeniency = 0.7f;

    /// <summary>Set once <see cref="ChartComputations.ApplyStacking"/> has run, so re-created
    /// chart layers over the same parsed beatmap don't stack twice.</summary>
    public bool StackingApplied;

    public List<ChartTimingPoint> TimingPoints = new();
    public List<ChartHitObject> HitObjects = new();

    /// <summary>Circle radius in osu-pixels: 54.4 − 4.48·CS.</summary>
    public float CircleRadius => 54.4f - 4.48f * CircleSize;

    /// <summary>Approach-circle preempt time in ms (osu!'s standard AR mapping).</summary>
    public double PreemptMs => ApproachRate <= 5
        ? 1200 + 600 * (5 - ApproachRate) / 5
        : 1200 - 750 * (ApproachRate - 5) / 5;

    /// <summary>Hit-object fade-in duration in ms, scaled off preempt the way osu! does.</summary>
    public double FadeInMs => 400 * PreemptMs / 1200;

    /// <summary>Beat length (ms/beat) of the controlling uninherited timing point at <paramref name="time"/>.</summary>
    public double BeatLengthAt(double time)
    {
        double result = double.NaN;

        foreach (var tp in TimingPoints)
        {
            if (!tp.Uninherited)
                continue;

            if (tp.Time <= time || double.IsNaN(result))
                result = tp.BeatLength;

            if (tp.Time > time && !double.IsNaN(result))
                break;
        }

        return double.IsNaN(result) ? 500 : result;
    }

    /// <summary>Slider-velocity multiplier from the latest inherited timing point at <paramref name="time"/> (1 when none applies).</summary>
    public double SliderVelocityAt(double time)
    {
        double sv = 1;
        double svTime = double.NegativeInfinity;

        foreach (var tp in TimingPoints)
        {
            if (tp.Time > time)
                break;

            if (tp.Uninherited)
            {
                // A new uninherited section resets SV to 1 until an inherited point overrides it.
                sv = 1;
                svTime = tp.Time;
            }
            else if (tp.Time >= svTime)
            {
                sv = tp.SvMultiplier;
                svTime = tp.Time;
            }
        }

        return sv;
    }

    /// <summary>The latest timing point (of either kind) at <paramref name="time"/>, for sample set / volume.</summary>
    public ChartTimingPoint? SamplePointAt(double time)
    {
        ChartTimingPoint? result = null;

        foreach (var tp in TimingPoints)
        {
            if (tp.Time > time && result != null)
                break;

            if (tp.Time <= time || result == null)
                result = tp;
        }

        return result;
    }
}

/// <summary>
/// Minimal, fault-tolerant .osu (v14-era) parser for [Difficulty], [TimingPoints] and
/// [HitObjects]. Malformed lines are skipped (and logged) — never allowed to fail the whole
/// parse, since maps come from arbitrary third-party mirrors.
/// </summary>
public static class BeatmapParser
{
    public static ChartBeatmap Parse(string osuPath) => ParseLines(File.ReadLines(osuPath));

    public static ChartBeatmap ParseLines(IEnumerable<string> lines)
    {
        var beatmap = new ChartBeatmap();
        string? section = null;
        bool arSet = false;
        float overallDifficulty = 5;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//"))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line.Substring(1, line.Length - 2);
                continue;
            }

            try
            {
                switch (section)
                {
                    case "General":
                        int genColon = line.IndexOf(':');
                        if (genColon < 0)
                            break;

                        if (line.Substring(0, genColon).Trim() == "StackLeniency")
                            float.TryParse(line.Substring(genColon + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out beatmap.StackLeniency);
                        break;

                    case "Difficulty":
                        int colon = line.IndexOf(':');
                        if (colon < 0)
                            break;

                        string key = line.Substring(0, colon).Trim();
                        string value = line.Substring(colon + 1).Trim();

                        switch (key)
                        {
                            case "CircleSize":
                                float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out beatmap.CircleSize);
                                break;

                            case "ApproachRate":
                                arSet = float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out beatmap.ApproachRate);
                                break;

                            case "OverallDifficulty":
                                float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out overallDifficulty);
                                break;

                            case "SliderMultiplier":
                                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out beatmap.SliderMultiplier);
                                break;

                            case "SliderTickRate":
                                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out beatmap.SliderTickRate);
                                break;
                        }

                        break;

                    case "TimingPoints":
                        var tp = parseTimingPoint(line);
                        if (tp != null)
                            beatmap.TimingPoints.Add(tp);
                        break;

                    case "HitObjects":
                        var obj = parseHitObject(line);
                        if (obj != null)
                            beatmap.HitObjects.Add(obj);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"BeatmapParser: skipping malformed line '{line}' ({ex.Message})", LoggingTarget.Runtime, LogLevel.Important);
            }
        }

        // Very old maps have no ApproachRate key — it mirrored OverallDifficulty back then.
        if (!arSet)
            beatmap.ApproachRate = overallDifficulty;

        beatmap.TimingPoints = beatmap.TimingPoints.OrderBy(p => p.Time).ToList();
        beatmap.HitObjects = beatmap.HitObjects.OrderBy(o => o.Time).ToList();

        // Slider end times need the fully-parsed timing points, so resolve them in a post-pass.
        foreach (var slider in beatmap.HitObjects.Where(o => o.Kind == HitObjectKind.Slider))
        {
            double beatLength = beatmap.BeatLengthAt(slider.Time);
            double sv = beatmap.SliderVelocityAt(slider.Time);
            double velocity = beatmap.SliderMultiplier * 100 * sv; // osu-px per beat

            slider.SpanDuration = velocity > 0 ? slider.PixelLength / velocity * beatLength : 0;
            slider.EndTime = slider.Time + slider.SpanDuration * Math.Max(1, slider.Slides);
        }

        return beatmap;
    }

    private static ChartTimingPoint? parseTimingPoint(string line)
    {
        string[] parts = line.Split(',');
        if (parts.Length < 2)
            return null;

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double time) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double beatLength))
        {
            return null;
        }

        var tp = new ChartTimingPoint { Time = time, BeatLength = beatLength };

        // Field 6 (uninherited) is authoritative when present; otherwise fall back to the sign
        // convention (positive = uninherited, negative = inherited SV).
        if (parts.Length > 6 && int.TryParse(parts[6], out int uninherited))
            tp.Uninherited = uninherited == 1;
        else
            tp.Uninherited = beatLength > 0;

        if (!tp.Uninherited && beatLength < 0)
            tp.SvMultiplier = -100 / beatLength;

        if (parts.Length > 3 && int.TryParse(parts[3], out int sampleSet))
            tp.SampleSet = sampleSet;
        if (parts.Length > 4 && int.TryParse(parts[4], out int sampleIndex))
            tp.SampleIndex = sampleIndex;
        if (parts.Length > 5 && int.TryParse(parts[5], out int volume))
            tp.Volume = volume;

        return tp;
    }

    private static ChartHitObject? parseHitObject(string line)
    {
        string[] parts = line.Split(',');
        if (parts.Length < 5)
            return null;

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double time) ||
            !int.TryParse(parts[3], out int type) ||
            !int.TryParse(parts[4], out int hitSound))
        {
            return null;
        }

        var obj = new ChartHitObject
        {
            X = x,
            Y = y,
            Time = time,
            EndTime = time,
            HitSound = hitSound,
            NewCombo = (type & 4) != 0,
        };

        if ((type & 1) != 0)
        {
            obj.Kind = HitObjectKind.Circle;
            return obj;
        }

        if ((type & 2) != 0)
        {
            if (parts.Length < 8)
                return null;

            obj.Kind = HitObjectKind.Slider;

            // curveType|p1|p2|... — points are "x:y".
            string[] curve = parts[5].Split('|');
            obj.CurveType = curve.Length > 0 && curve[0].Length > 0 ? curve[0][0] : 'B';
            obj.ControlPoints.Add(new Vector2(x, y));

            foreach (string p in curve.Skip(1))
            {
                string[] xy = p.Split(':');
                if (xy.Length != 2 ||
                    !float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float px) ||
                    !float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float py))
                {
                    return null;
                }

                obj.ControlPoints.Add(new Vector2(px, py));
            }

            if (!int.TryParse(parts[6], out int slides) || slides < 1 ||
                !double.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out double length) ||
                length <= 0 || !double.IsFinite(length))
            {
                return null;
            }

            obj.Slides = slides;
            obj.PixelLength = length;

            if (parts.Length > 8)
            {
                int[] edges = parts[8].Split('|')
                                      .Select(s => int.TryParse(s, out int e) ? e : 0)
                                      .ToArray();
                if (edges.Length > 0)
                    obj.EdgeSounds = edges;
            }

            return obj;
        }

        if ((type & 8) != 0)
        {
            if (parts.Length < 6 ||
                !double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double endTime))
            {
                return null;
            }

            obj.Kind = HitObjectKind.Spinner;
            obj.X = 256;
            obj.Y = 192;
            obj.EndTime = Math.Max(time, endTime);
            return obj;
        }

        return null; // unknown type bits (e.g. mania holds) — not renderable here
    }
}
