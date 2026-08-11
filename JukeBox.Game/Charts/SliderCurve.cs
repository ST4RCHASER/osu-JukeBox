#nullable enable

using System;
using System.Collections.Generic;
using osuTK;

namespace JukeBox.Game.Charts;

/// <summary>
/// Approximate slider-curve flattening for display purposes: linear passthrough, de Casteljau
/// bezier sampling (with osu!'s duplicated-point segment convention), perfect-circle arcs via the
/// 3-point circumcircle, and catmull falling back to bezier. The polyline is trimmed to the
/// slider's pixel length. This is a jukebox visualiser, not a ruleset — small deviations from
/// osu!'s exact path algorithm are acceptable.
/// </summary>
public static class SliderCurve
{
    /// <summary>Points per bezier segment — plenty for smooth display at playfield scale.</summary>
    private const int bezier_samples_per_segment = 50;

    public static List<Vector2> Sample(char curveType, IReadOnlyList<Vector2> controlPoints, double pixelLength)
    {
        if (controlPoints.Count < 2)
            return new List<Vector2>(controlPoints);

        List<Vector2> path;

        switch (char.ToUpperInvariant(curveType))
        {
            case 'L':
                path = new List<Vector2>(controlPoints);
                break;

            case 'P' when controlPoints.Count == 3:
                path = sampleCircularArc(controlPoints[0], controlPoints[1], controlPoints[2]);
                break;

            // 'B', 'C' (catmull → bezier approximation, per spec), and degenerate 'P' counts.
            default:
                path = sampleBezierSegments(controlPoints);
                break;
        }

        return trimToLength(path, pixelLength);
    }

    /// <summary>
    /// osu! bezier convention: a control point repeated twice in a row starts a new bezier
    /// segment at that point. Each segment is sampled with de Casteljau evaluation.
    /// </summary>
    private static List<Vector2> sampleBezierSegments(IReadOnlyList<Vector2> points)
    {
        var result = new List<Vector2>();
        var segment = new List<Vector2>();

        for (int i = 0; i < points.Count; i++)
        {
            segment.Add(points[i]);

            bool segmentEnds = i == points.Count - 1 ||
                               (i < points.Count - 1 && points[i] == points[i + 1]);

            if (!segmentEnds)
                continue;

            appendBezier(result, segment);
            segment.Clear();
        }

        return result;
    }

    private static void appendBezier(List<Vector2> output, List<Vector2> controlPoints)
    {
        if (controlPoints.Count == 0)
            return;

        if (controlPoints.Count == 1)
        {
            output.Add(controlPoints[0]);
            return;
        }

        var buffer = new Vector2[controlPoints.Count];

        for (int step = 0; step <= bezier_samples_per_segment; step++)
        {
            float t = step / (float)bezier_samples_per_segment;

            // de Casteljau in-place reduction.
            for (int i = 0; i < controlPoints.Count; i++)
                buffer[i] = controlPoints[i];

            for (int level = controlPoints.Count - 1; level > 0; level--)
            {
                for (int i = 0; i < level; i++)
                    buffer[i] = Vector2.Lerp(buffer[i], buffer[i + 1], t);
            }

            output.Add(buffer[0]);
        }
    }

    /// <summary>Arc through three points via their circumcircle; collinear points degrade to a line.</summary>
    private static List<Vector2> sampleCircularArc(Vector2 a, Vector2 b, Vector2 c)
    {
        float d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));

        if (Math.Abs(d) < 1e-3f)
            return new List<Vector2> { a, b, c }; // (nearly) collinear — treat as linear

        float aSq = a.LengthSquared, bSq = b.LengthSquared, cSq = c.LengthSquared;

        var centre = new Vector2(
            (aSq * (b.Y - c.Y) + bSq * (c.Y - a.Y) + cSq * (a.Y - b.Y)) / d,
            (aSq * (c.X - b.X) + bSq * (a.X - c.X) + cSq * (b.X - a.X)) / d);

        float radius = (a - centre).Length;

        double startAngle = Math.Atan2(a.Y - centre.Y, a.X - centre.X);
        double midAngle = Math.Atan2(b.Y - centre.Y, b.X - centre.X);
        double endAngle = Math.Atan2(c.Y - centre.Y, c.X - centre.X);

        // Sweep from start through mid to end: normalize both deltas into the same rotational
        // direction so the arc actually passes through the middle control point.
        double toMid = normalizeAngle(midAngle - startAngle);
        double toEnd = normalizeAngle(endAngle - startAngle);

        if (toMid < 0 != toEnd < 0 || Math.Abs(toMid) > Math.Abs(toEnd))
            toEnd += toEnd < 0 ? 2 * Math.PI : -2 * Math.PI;

        const int samples = 60;
        var result = new List<Vector2>(samples + 1);

        for (int i = 0; i <= samples; i++)
        {
            double angle = startAngle + toEnd * i / samples;
            result.Add(centre + new Vector2(radius * (float)Math.Cos(angle), radius * (float)Math.Sin(angle)));
        }

        return result;
    }

    private static double normalizeAngle(double angle)
    {
        while (angle > Math.PI) angle -= 2 * Math.PI;
        while (angle < -Math.PI) angle += 2 * Math.PI;
        return angle;
    }

    /// <summary>Cuts the polyline at <paramref name="pixelLength"/> along its arc length (no extension).</summary>
    private static List<Vector2> trimToLength(List<Vector2> path, double pixelLength)
    {
        if (path.Count < 2 || pixelLength <= 0)
            return path;

        var result = new List<Vector2> { path[0] };
        double travelled = 0;

        for (int i = 1; i < path.Count; i++)
        {
            float segLength = (path[i] - path[i - 1]).Length;

            if (segLength <= 0)
                continue;

            if (travelled + segLength >= pixelLength)
            {
                float t = (float)((pixelLength - travelled) / segLength);
                result.Add(Vector2.Lerp(path[i - 1], path[i], t));
                return result;
            }

            travelled += segLength;
            result.Add(path[i]);
        }

        return result;
    }
}
