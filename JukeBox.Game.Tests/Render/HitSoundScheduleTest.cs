#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JukeBox.Game.Replays;
using JukeBox.Game.UI.Render;
using NUnit.Framework;
using osu.Game.Audio;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Scoring;
using osuTK;

namespace JukeBox.Game.Tests.Render
{
    /// <summary>
    /// What a render's audio SOUNDS and WHEN, off a real fixture map: autoplay sounds every object
    /// (slider edges and ticks included, the end sound off the slider's own tail samples), a
    /// judgement schedule sounds only what the play hit, and the render range clips both. All pure —
    /// no audio, host or renderer.
    /// </summary>
    [TestFixture]
    public class HitSoundScheduleTest
    {
        private static IBeatmap playable()
        {
            string osuFile = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "happy_people_easy.osu");
            return new FlatWorkingBeatmap(osuFile).GetPlayableBeatmap(new OsuRuleset().RulesetInfo, Array.Empty<Mod>());
        }

        /// <summary>A one-slider beatmap whose END sound (last node: a drum finish) DIFFERS from its
        /// head/body samples — the distinction that proves the end sounds the slider's
        /// <see cref="Slider.TailSamples"/> and not just any sample list that happens to match.</summary>
        private static (IBeatmap beatmap, Slider slider) sliderWithADistinctEndSound()
        {
            var slider = new Slider
            {
                StartTime = 1000,
                Path = new SliderPath(PathType.LINEAR, new[] { Vector2.Zero, new Vector2(100, 0) }),
                Samples = new List<HitSampleInfo> { new HitSampleInfo(HitSampleInfo.HIT_NORMAL) },
                NodeSamples = new List<IList<HitSampleInfo>>
                {
                    new List<HitSampleInfo> { new HitSampleInfo(HitSampleInfo.HIT_NORMAL) },
                    new List<HitSampleInfo> { new HitSampleInfo(HitSampleInfo.HIT_FINISH, HitSampleInfo.BANK_DRUM) },
                },
            };

            slider.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty());

            var beatmap = new Beatmap { HitObjects = { slider } };

            Assert.That(slider.TailSamples, Is.Not.Empty);
            Assert.That(slider.TailSamples.SequenceEqual(slider.Samples), Is.False, "the end sound must be distinguishable");

            return (beatmap, slider);
        }

        [Test]
        public void AutoplaySoundsEveryObjectInTimeOrderWithSamples()
        {
            var beatmap = playable();
            var schedule = HitSoundSchedule.ForAutoplay(beatmap, 0, double.MaxValue);

            Assert.That(schedule, Is.Not.Empty);
            Assert.That(schedule.Select(e => e.TimeMs), Is.Ordered);
            Assert.That(schedule, Has.All.Matches<HitSoundSchedule.Entry>(e => e.Samples.Count > 0));

            // Sliders sound more than once (head + tail at least), so the schedule outnumbers the
            // top-level objects on any map with a slider in it.
            Assert.That(beatmap.HitObjects.OfType<Slider>().Any(), Is.True, "fixture should contain sliders");
            Assert.That(schedule.Count, Is.GreaterThan(beatmap.HitObjects.Count));
        }

        [Test]
        public void AutoplaySoundsASlidersEndOffItsTailSamplesAtItsEndTime()
        {
            var (beatmap, slider) = sliderWithADistinctEndSound();

            var schedule = HitSoundSchedule.ForAutoplay(beatmap, 0, double.MaxValue);
            var atEnd = schedule.Where(e => Math.Abs(e.TimeMs - slider.GetEndTime()) < 0.001).ToList();

            Assert.That(atEnd.Any(e => e.Samples.SequenceEqual(slider.TailSamples)), Is.True, "the end must sound the last node's drum finish");
            Assert.That(atEnd.Any(e => e.Samples.SequenceEqual(slider.Samples)), Is.False, "not the slider's own (body) samples");
        }

        [Test]
        public void TheRangeClipsTheSchedule()
        {
            var beatmap = playable();
            var whole = HitSoundSchedule.ForAutoplay(beatmap, 0, double.MaxValue);

            double from = whole[whole.Count / 4].TimeMs;
            double to = whole[whole.Count / 2].TimeMs;

            var clipped = HitSoundSchedule.ForAutoplay(beatmap, from, to);

            Assert.That(clipped, Is.Not.Empty);
            Assert.That(clipped.Count, Is.LessThan(whole.Count));
            Assert.That(clipped, Has.All.Matches<HitSoundSchedule.Entry>(e => e.TimeMs >= from && e.TimeMs <= to));
        }

        [Test]
        public void JudgementsSoundOnlyWhatWasHitAtTheJudgedTime()
        {
            var beatmap = playable();
            var circles = beatmap.HitObjects.OfType<HitCircle>().Where(c => c is not SliderHeadCircle).Take(2).ToList();
            Assert.That(circles, Has.Count.EqualTo(2), "fixture should contain plain circles");

            // The first circle hit (a touch late), the second missed.
            var judged = new[]
            {
                new AnalyticOsuJudge.Judged(circles[0], HitResult.Great, circles[0].StartTime + 12, Vector2.Zero),
                new AnalyticOsuJudge.Judged(circles[1], HitResult.Miss, circles[1].StartTime, Vector2.Zero),
            };

            var schedule = HitSoundSchedule.ForJudgements(beatmap, judged, 0, double.MaxValue);

            Assert.That(schedule, Has.Count.EqualTo(1));
            Assert.That(schedule[0].TimeMs, Is.EqualTo(circles[0].StartTime + 12));
            Assert.That(schedule[0].Samples, Is.EqualTo(circles[0].Samples));
        }

        [Test]
        public void ACompletedSliderSoundsItsTailOnceAndItsAggregateNever()
        {
            var (beatmap, slider) = sliderWithADistinctEndSound();
            var tail = slider.NestedHitObjects.OfType<SliderTailCircle>().Single();

            // The tail tick landed AND the slider's aggregate judged (as a real play produces) —
            // only the tail may sound, off the slider's tail samples, or the end sound doubles.
            var judged = new[]
            {
                new AnalyticOsuJudge.Judged(tail, HitResult.SmallTickHit, tail.StartTime, Vector2.Zero),
                new AnalyticOsuJudge.Judged(slider, HitResult.Great, slider.GetEndTime(), Vector2.Zero),
            };

            var schedule = HitSoundSchedule.ForJudgements(beatmap, judged, 0, double.MaxValue);

            Assert.That(schedule, Has.Count.EqualTo(1));
            Assert.That(schedule[0].Samples, Is.EqualTo(slider.TailSamples), "the end must sound the last node's drum finish, once");
        }

        [Test]
        public void MissesEverywhereSoundNothing()
        {
            var beatmap = playable();

            var judged = beatmap.HitObjects.OfType<HitCircle>()
                                .Select(c => new AnalyticOsuJudge.Judged(c, HitResult.Miss, c.StartTime, Vector2.Zero))
                                .ToList();

            Assert.That(HitSoundSchedule.ForJudgements(beatmap, judged, 0, double.MaxValue), Is.Empty);
        }

        [Test]
        public void NoJudgementsSoundNothing()
        {
            Assert.That(HitSoundSchedule.ForJudgements(playable(), new List<AnalyticOsuJudge.Judged>(), 0, double.MaxValue), Is.Empty);
        }
    }
}
