#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
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
        var reader = new OsuFileReader(path);
        var vars = new VariableCollection(new VariableReader(reader).EnumValues());
        var events = new EventReader(reader, vars);
        var objs = new StoryboardReader(events).EnumValues().Where(o => o != null).ToList();

        foreach (var o in objs)
            o.CalculateAndApplyBaseFrameTime();

        return objs;
    }
}
