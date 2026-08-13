#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using osu.Framework.Logging;
using ReOsuStoryboardPlayer.Core.Base;
using ReOsuStoryboardPlayer.Core.Optimzer;
using ReOsuStoryboardPlayer.Core.Optimzer.DefaultOptimzer;
using ReOsuStoryboardPlayer.Core.Parser.Collection;
using ReOsuStoryboardPlayer.Core.Parser.Reader;
using ReOsuStoryboardPlayer.Core.Parser.Stream;

namespace JukeBox.Game.Storyboard;

/// <summary>
/// Parses a storyboard's .osb and .osu files via Core's reader chain and merges them into a
/// single Z-ordered object list, ready to be compiled into framework transforms by
/// TransformStoryboardLayer. Pure CPU work with no framework dependency — call this off the
/// update thread (e.g. from a BackgroundDependencyLoader).
/// </summary>
public static class StoryboardLoader
{
    public static List<StoryboardObject> Load(string? osbFile, string? osuFile)
    {
        // Explicitly OFF (the Core default), and it must stay off: Core's unrolling
        // (LoopCommand.SubCommandExpand) allocates LoopCount copies of every sub-command with no
        // upper bound, so a hostile .osb with "L,0,2000000000" would OOM/hang inside parse — and
        // no exception means the malformed-storyboard try/catch can't save us. Loops are instead
        // compiled into framework transform-loops (Transform.LoopCount — O(1) per sub-command
        // regardless of iteration count) by StoryboardTransforms. The flag is read in
        // StoryboardObject.AddLoopCommand during parse, so it's pinned before parsing starts.
        ReOsuStoryboardPlayer.Setting.EnableLoopCommandUnrolling = false;

        var osb = osbFile != null ? readFile(osbFile) : new List<StoryboardObject>();
        var osu = osuFile != null ? readFile(osuFile) : new List<StoryboardObject>();

        var result = new List<StoryboardObject>();

        // Per-layer merge order matches osu!'s own storyboard compositing: for each layer, the
        // difficulty's own (.osu) events render first (behind), then .osb events render on top.
        foreach (Layer layer in Enum.GetValues<Layer>())
        {
            result.AddRange(osu.Where(o => o.layer == layer));
            result.AddRange(osb.Where(o => o.layer == layer).Select(o =>
            {
                o.FromOsbFile = true;
                return o;
            }));
        }

        int z = 0;
        foreach (var obj in result)
            obj.Z = z++;

        StoryboardOptimzerManager.AddOptimzer<RuntimeOptimzer>();
        StoryboardOptimzerManager.Optimze(2, result);

        return result;
    }

    private static List<StoryboardObject> readFile(string path)
    {
        using var stream = normalizeAnimationObjectLines(path);
        var reader = new OsuFileReader(stream);
        var vars = new VariableCollection(new VariableReader(reader).EnumValues());
        var events = new EventReader(reader, vars);
        var storyboardReader = new StoryboardReader(events);

        // Core's EnumValues() is a lazy Select over one packet per object; a single malformed
        // object (e.g. a truncated/garbage column) throws out of the selector and would abort the
        // whole enumeration via a plain foreach/ToList, dropping every object still to come.
        // Stepping the enumerator by hand lets us catch per-MoveNext() and skip just that object —
        // the underlying line reader is unaffected by the failure and resumes cleanly at the next
        // packet. The caller's whole-file try/catch remains the last resort for failures outside
        // this loop (e.g. the variable/section pre-pass above).
        var objs = new List<StoryboardObject>();
        int skipped = 0;

        using (var e = storyboardReader.EnumValues().GetEnumerator())
        {
            while (true)
            {
                StoryboardObject? current;

                try
                {
                    if (!e.MoveNext())
                        break;

                    current = e.Current;
                }
                catch (Exception)
                {
                    skipped++;
                    continue;
                }

                if (current != null)
                    objs.Add(current);
            }
        }

        if (skipped > 0)
            Logger.Log($"StoryboardLoader: skipped {skipped} malformed storyboard object(s) in '{Path.GetFileName(path)}'");

        foreach (var o in objs)
            o.CalculateAndApplyBaseFrameTime();

        return objs;
    }

    // osu's spec allows an Animation object line to omit its trailing loopType column
    // (Animation,layer,origin,path,x,y,frameCount,frameDelay — 8 columns), defaulting it to
    // LoopForever. Core's ParseStoryboardAnimation indexes that column unconditionally
    // (sprite_param[8]) and throws IndexOutOfRangeException when it's missing, which used to take
    // the whole file down via the whole-file catch in TransformStoryboardLayer. Rather than patch
    // the Core submodule, rewrite just those lines before handing the file to Core's reader chain.
    // Command lines (leading '_'/space), comments, blanks and section headers are left untouched;
    // only [Events]-section Animation object lines with exactly 8 columns are touched.
    private static Stream normalizeAnimationObjectLines(string path)
    {
        var lines = File.ReadAllLines(path);
        bool inEvents = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.Length > 2 && line[0] == '[' && line[^1] == ']')
            {
                inEvents = string.Equals(line[1..^1], "Events", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inEvents || line.Length == 0 || line[0] == '_' || line[0] == ' ' || line.StartsWith("//"))
                continue;

            if (!line.StartsWith("Animation,", StringComparison.OrdinalIgnoreCase))
                continue;

            if (countCsvColumns(line) == 8)
                lines[i] = line + ",LoopForever";
        }

        return new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
    }

    // Comma-splits like the Core parser does, but ignores commas inside a quoted field (the path
    // column, e.g. "my,file.png", is the only field the format allows to contain a literal comma).
    private static int countCsvColumns(string line)
    {
        int count = 1;
        bool inQuotes = false;

        foreach (char c in line)
        {
            if (c == '"')
                inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
                count++;
        }

        return count;
    }
}
