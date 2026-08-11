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
        // Must be set BEFORE parsing: StoryboardObject.AddLoopCommand reads this flag at parse
        // time (inside StoryboardReader's command build). With unrolling, Core materializes every
        // loop iteration into plain commands on the object's main timelines, so the transform
        // compiler never has to translate "L" loops into framework transform-loops.
        ReOsuStoryboardPlayer.Setting.EnableLoopCommandUnrolling = true;

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
