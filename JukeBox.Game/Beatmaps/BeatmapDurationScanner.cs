#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using osu.Framework.Logging;

namespace JukeBox.Game.Beatmaps;

/// <summary>
/// Computes how long a beatmap's CONTENT runs, for sets that have no audio file to take a length
/// from (see <see cref="CachedBeatmapSet.HasVirtualAudio"/>). The silent track
/// <see cref="Playback.PlaybackController"/> plays for those sets is sized from this, so the clock
/// runs exactly as long as there is something to hear or see.
/// </summary>
public static class BeatmapDurationScanner
{
    /// <summary>Played after the last hit object / storyboard event, so the final keysound and the
    /// storyboard's last frames aren't cut off by the track completing on top of them.</summary>
    public const double TailMs = 3000;

    /// <summary>
    /// Length for <paramref name="set"/>'s silent track: the furthest point reached by
    /// <paramref name="osuFile"/>'s hit objects, its [Events] storyboard, and the set's .osb,
    /// plus <see cref="TailMs"/>. Never returns less than <see cref="TailMs"/> — a set whose
    /// content can't be read at all still gets a clock that runs, rather than one that completes
    /// instantly and spins the queue.
    /// </summary>
    public static double ComputeLength(CachedBeatmapSet set, string? osuFile)
    {
        double end = 0;

        if (osuFile != null)
            end = Math.Max(end, ScanEndTime(osuFile));

        if (set.OsbFile != null)
            end = Math.Max(end, ScanEndTime(set.OsbFile));

        return end + TailMs;
    }

    /// <summary>
    /// The last time referenced by an .osu or .osb file: hit object times (including spinner and
    /// mania-hold end times), storyboard <c>Sample</c> events, and storyboard command end times.
    /// Returns 0 when the file can't be read.
    /// </summary>
    public static double ScanEndTime(string path)
    {
        try
        {
            return ScanEndTime(File.ReadLines(path));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"BeatmapDurationScanner: failed to read '{path}'; contributing 0 to the virtual track length");
            return 0;
        }
    }

    /// <summary>The line-level half of <see cref="ScanEndTime(string)"/>.</summary>
    public static double ScanEndTime(IEnumerable<string> lines)
    {
        double end = 0;
        string? section = null;

        // A storyboard loop's inner commands are timed RELATIVE to the loop start and repeat
        // loopCount times, so the loop's real end is only known once its last inner command has
        // been seen. These carry that pending loop until the next top-level line closes it.
        double loopStart = 0;
        int loopCount = 0;
        double loopInnerEnd = 0;
        bool inLoop = false;

        void closeLoop()
        {
            if (!inLoop)
                return;

            end = Math.Max(end, loopStart + Math.Max(loopCount, 1) * loopInnerEnd);
            inLoop = false;
        }

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                closeLoop();
                section = line.Substring(1, line.Length - 2);
                continue;
            }

            switch (section)
            {
                case "Events":
                    // Indentation (spaces or underscores, both legal in the format) is what
                    // separates a storyboard command from the object it belongs to, and a
                    // loop-relative command from an absolute one.
                    int depth = indentDepth(rawLine);
                    string[] parts = line.Split(',');

                    if (depth == 0)
                    {
                        closeLoop();

                        // Sample,<time>,<layer>,"<file>",<volume> — the only [Events] line whose
                        // audio matters here; Sprite/Animation/background/video carry no time.
                        if (parts.Length >= 2 && parts[0].Trim().Equals("Sample", StringComparison.OrdinalIgnoreCase))
                            end = Math.Max(end, parseTime(parts[1]));

                        break;
                    }

                    string command = parts[0].Trim();

                    if (depth == 1 && command.Equals("L", StringComparison.Ordinal))
                    {
                        // L,<starttime>,<loopcount>
                        closeLoop();
                        if (parts.Length >= 3)
                        {
                            loopStart = parseTime(parts[1]);
                            loopCount = (int)parseTime(parts[2]);
                            loopInnerEnd = 0;
                            inLoop = true;
                        }
                        break;
                    }

                    if (depth == 1 && command.Equals("T", StringComparison.Ordinal))
                    {
                        // T,<trigger>,<starttime>,<endtime> — fires at most until endtime.
                        closeLoop();
                        if (parts.Length >= 4)
                            end = Math.Max(end, Math.Max(parseTime(parts[2]), parseTime(parts[3])));
                        break;
                    }

                    // <event>,<easing>,<starttime>,<endtime>,... — endtime may be blank, which
                    // the format defines as "same as starttime".
                    if (parts.Length < 4)
                        break;

                    double commandEnd = Math.Max(parseTime(parts[2]), parseTime(parts[3]));

                    if (inLoop && depth >= 2)
                        loopInnerEnd = Math.Max(loopInnerEnd, commandEnd);
                    else
                        end = Math.Max(end, commandEnd);

                    break;

                case "HitObjects":
                    string[] fields = line.Split(',');
                    if (fields.Length < 4)
                        break;

                    double time = parseTime(fields[2]);
                    end = Math.Max(end, time);

                    if (!int.TryParse(fields[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int type))
                        break;

                    // Spinners (bit 3) and mania holds (bit 7) carry their own end time in the
                    // first object param; the hold's is ':'-joined with its hit sample. Sliders
                    // deliberately aren't extended — their duration needs the full timing-point
                    // and slider-velocity model, and TailMs covers the overhang either way.
                    const int spinner = 8;
                    const int hold = 128;

                    if ((type & (spinner | hold)) != 0 && fields.Length >= 6)
                    {
                        string param = fields[5];
                        int colon = param.IndexOf(':');
                        if (colon >= 0)
                            param = param.Substring(0, colon);

                        end = Math.Max(end, parseTime(param));
                    }

                    break;
            }
        }

        closeLoop();
        return end;
    }

    private static int indentDepth(string rawLine)
    {
        int depth = 0;
        foreach (char c in rawLine)
        {
            if (c == ' ' || c == '_')
                depth++;
            else
                break;
        }

        return depth;
    }

    private static double parseTime(string value)
        => double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;
}
