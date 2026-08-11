#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Beatmaps;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Graphics;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;

namespace JukeBox.Game.Charts;

/// <summary>
/// Plays hitsounds at each object's hit time for one difficulty. NOT transform-based (samples
/// aren't rewindable): a sorted-times cursor advances in <see cref="Update"/> and fires every
/// event whose time passed since the last frame; a backward seek re-binary-searches the cursor
/// so replayed sections sound again; a large forward seek suppresses the would-be catch-up burst.
/// </summary>
public partial class HitSoundPlayer : Component
{
    /// <summary>Fixed attenuation so hitsounds sit under the music.</summary>
    private const double master_volume = 0.7;

    private static readonly string[] set_names = { "normal", "normal", "soft", "drum" };

    private readonly List<HitSoundEvent> events = new();
    private readonly HitSoundCursor cursor;
    private readonly CachedBeatmapSet set;

    private ISampleStore? mapSamples;

    // Sample lookups walk the map folder with multiple candidate names; memoize per resolved name
    // (misses cached as null too) so repeated hitsounds don't re-pay the probe chain.
    private readonly Dictionary<string, Sample?> sampleCache = new();

    [Resolved]
    private AudioManager audio { get; set; } = null!;

    [Resolved]
    private GameHost host { get; set; } = null!;

    /// <summary>Number of hitsound events scheduled. Exposed for tests.</summary>
    internal int EventCount => events.Count;

    public HitSoundPlayer(ChartBeatmap beatmap, CachedBeatmapSet set)
    {
        this.set = set;

        foreach (var obj in beatmap.HitObjects)
        {
            switch (obj.Kind)
            {
                case HitObjectKind.Circle:
                    addEvent(beatmap, obj.Time, obj.HitSound);
                    break;

                case HitObjectKind.Slider:
                    // Head, each repeat, tail — edgeSounds when the map provides them.
                    int edges = Math.Max(1, obj.Slides) + 1;

                    for (int i = 0; i < edges; i++)
                    {
                        int hitSound = obj.EdgeSounds != null && i < obj.EdgeSounds.Length
                            ? obj.EdgeSounds[i]
                            : obj.HitSound;
                        addEvent(beatmap, obj.Time + obj.SpanDuration * i, hitSound);
                    }

                    break;

                case HitObjectKind.Spinner:
                    addEvent(beatmap, obj.EndTime, obj.HitSound);
                    break;
            }
        }

        events.Sort((a, b) => a.Time.CompareTo(b.Time));
        cursor = new HitSoundCursor(events.Select(e => e.Time).ToArray());
    }

    private void addEvent(ChartBeatmap beatmap, double time, int hitSound)
    {
        var samplePoint = beatmap.SamplePointAt(time);

        events.Add(new HitSoundEvent
        {
            Time = time,
            HitSound = hitSound,
            SampleSet = samplePoint?.SampleSet ?? 0,
            SampleIndex = samplePoint?.SampleIndex ?? 0,
            Volume = samplePoint?.Volume ?? 100,
        });
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        mapSamples = audio.GetSampleStore(new StorageBackedResourceStore(new NativeStorage(set.Directory, host)));
    }

    protected override void Update()
    {
        base.Update();

        foreach (int index in cursor.Advance(Time.Current))
            play(events[index]);
    }

    private void play(HitSoundEvent ev)
    {
        string setName = set_names[Math.Clamp(ev.SampleSet, 0, 3)];
        double volume = Math.Clamp(ev.Volume, 0, 100) / 100.0 * master_volume;

        // Bit 0 (normal) always sounds; whistle/finish/clap layer on top when set.
        playSample(setName, "normal", ev.SampleIndex, volume);

        if ((ev.HitSound & 2) != 0)
            playSample(setName, "whistle", ev.SampleIndex, volume);
        if ((ev.HitSound & 4) != 0)
            playSample(setName, "finish", ev.SampleIndex, volume);
        if ((ev.HitSound & 8) != 0)
            playSample(setName, "clap", ev.SampleIndex, volume);
    }

    private void playSample(string setName, string type, int index, double volume)
    {
        var sample = resolveSample(setName, type, index);
        if (sample == null)
            return;

        var channel = sample.GetChannel();
        channel.Volume.Value = volume;
        channel.Play();
    }

    private Sample? resolveSample(string setName, string type, int index)
    {
        string key = $"{setName}-hit{type}{index}";

        if (sampleCache.TryGetValue(key, out var cached))
            return cached;

        Sample? sample = null;

        if (mapSamples != null)
        {
            // Custom-indexed name first (index omitted when 0/1), then the map's default name.
            string[] baseNames = index > 1
                ? new[] { $"{setName}-hit{type}{index}", $"{setName}-hit{type}" }
                : new[] { $"{setName}-hit{type}" };

            foreach (string baseName in baseNames)
            {
                sample = mapSamples.Get($"{baseName}.wav") ?? mapSamples.Get($"{baseName}.ogg") ?? mapSamples.Get(baseName);
                if (sample != null)
                    break;
            }
        }

        // Universal fallback: the tiny click shipped in JukeBox.Resources/Samples, so the feature
        // is audible even on maps that bundle no hitsound files at all.
        sample ??= audio.Samples.Get("click");

        sampleCache[key] = sample;
        return sample;
    }

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        mapSamples?.Dispose();
    }

    private struct HitSoundEvent
    {
        public double Time;
        public int HitSound;
        public int SampleSet;
        public int SampleIndex;
        public int Volume;
    }
}

/// <summary>
/// Pure forward-cursor over a sorted list of event times. <see cref="Advance"/> returns the
/// indices of events that became due since the previous call; a backward jump rewinds the cursor
/// (binary search) so those events fire again; events that became due more than
/// <see cref="CatchUpWindowMs"/> before "now" (i.e. skipped over by a forward seek) are swallowed.
/// Separated from the drawable so it can be unit-tested headlessly.
/// </summary>
public class HitSoundCursor
{
    /// <summary>How stale an event may be and still fire — anything older was seeked over.</summary>
    public const double CatchUpWindowMs = 200;

    private readonly double[] times;
    private int index;
    private double lastTime = double.NegativeInfinity;

    public HitSoundCursor(double[] sortedTimes)
    {
        times = sortedTimes;
    }

    public List<int> Advance(double now)
    {
        var fired = new List<int>();

        if (now < lastTime)
        {
            // Backward seek: rewind to the first event strictly after `now` so the replayed
            // section fires again.
            int lo = Array.BinarySearch(times, now);
            if (lo < 0)
                lo = ~lo;
            else
            {
                // Exact match: include it (and any duplicates) for re-firing.
                while (lo > 0 && times[lo - 1] >= now)
                    lo--;
            }

            index = lo;
        }

        while (index < times.Length && times[index] <= now)
        {
            if (now - times[index] <= CatchUpWindowMs)
                fired.Add(index);

            index++;
        }

        lastTime = now;
        return fired;
    }
}
