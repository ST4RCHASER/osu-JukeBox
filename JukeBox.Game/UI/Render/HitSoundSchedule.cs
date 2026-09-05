#nullable enable

using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Replays;
using osu.Game.Audio;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Scoring;

namespace JukeBox.Game.UI.Render;

/// <summary>
/// The hitsounds one play of a beatmap produces: WHEN each sounds and WHAT sounds — lazer's own
/// <see cref="HitSampleInfo"/> descriptors, exactly as gameplay hands them to the skin. Built either
/// from a replay's analytic judgements (<see cref="ForJudgements"/> — only what the player actually
/// hit sounds, at the time they hit it, matching what the render's video shows) or from the beatmap
/// alone (<see cref="ForAutoplay"/> — every object sounds at its own time, the no-replay playback).
/// Pure over lazer's beatmap model, so <c>HitSoundScheduleTest</c> exercises it off a fixture map
/// with no audio, host or renderer anywhere near it.
/// </summary>
public static class HitSoundSchedule
{
    /// <summary>One scheduled hitsound: the playback time it lands at and the samples that sound
    /// there (an object usually layers several — hitnormal plus its whistle/finish/clap).</summary>
    public readonly record struct Entry(double TimeMs, IReadOnlyList<HitSampleInfo> Samples);

    /// <summary>
    /// The schedule of a perfect, replay-less playback over <paramref name="playable"/>: circles at
    /// their start times, slider heads/repeats/ticks at theirs, the slider's end sound
    /// (<see cref="Slider.TailSamples"/> — lazer keeps it on the slider, not the tail circle) at the
    /// slider's end, spinners at their end. Non-osu objects sound their samples at their start time.
    /// </summary>
    public static IReadOnlyList<Entry> ForAutoplay(IBeatmap playable, double startMs, double endMs)
    {
        var entries = new List<Entry>();

        foreach (var obj in playable.HitObjects)
        {
            switch (obj)
            {
                case Slider slider:
                    foreach (var nested in slider.NestedHitObjects)
                    {
                        if (nested is SliderTailCircle)
                            add(entries, slider.GetEndTime(), slider.TailSamples);
                        else
                            add(entries, nested.StartTime, nested.Samples);
                    }

                    break;

                case Spinner spinner:
                    add(entries, spinner.GetEndTime(), spinner.Samples);
                    break;

                default:
                    add(entries, obj.StartTime, obj.Samples);
                    break;
            }
        }

        return finish(entries, startMs, endMs);
    }

    /// <summary>
    /// The schedule of one REPLAY's playback: every judgement that landed a hit sounds its object's
    /// samples at the judged time; misses stay silent, exactly as gameplay would. Slider parts sound
    /// individually (head/repeats/ticks off their own nested objects, the end sound off the owning
    /// slider's <see cref="Slider.TailSamples"/>); the slider's aggregate judgement carries no sound
    /// of its own, so it is skipped rather than doubling the tail.
    /// </summary>
    public static IReadOnlyList<Entry> ForJudgements(IBeatmap playable, IReadOnlyList<AnalyticOsuJudge.Judged> judged, double startMs, double endMs)
    {
        // Tail circle → owning slider: the end sound lives on the slider (TailSamples), and the tail
        // circle's own sample list is not it.
        var tailOwner = new Dictionary<HitObject, Slider>();

        foreach (var slider in playable.HitObjects.OfType<Slider>())
        {
            foreach (var tail in slider.NestedHitObjects.OfType<SliderTailCircle>())
                tailOwner[tail] = slider;
        }

        var entries = new List<Entry>();

        foreach (var j in judged)
        {
            if (!j.Result.IsHit())
                continue;

            switch (j.Object)
            {
                case Slider:
                    break;

                case SliderTailCircle tail when tailOwner.TryGetValue(tail, out var owner):
                    add(entries, j.Time, owner.TailSamples);
                    break;

                default:
                    add(entries, j.Time, j.Object.Samples);
                    break;
            }
        }

        return finish(entries, startMs, endMs);
    }

    private static void add(List<Entry> entries, double timeMs, IList<HitSampleInfo> samples)
    {
        if (samples.Count > 0)
            entries.Add(new Entry(timeMs, samples.ToArray()));
    }

    /// <summary>Only what falls inside the render range, in time order.</summary>
    private static IReadOnlyList<Entry> finish(List<Entry> entries, double startMs, double endMs)
        => entries.Where(e => e.TimeMs >= startMs && e.TimeMs <= endMs)
                  .OrderBy(e => e.TimeMs)
                  .ToList();
}
