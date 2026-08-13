#nullable enable

using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Charts;
using NUnit.Framework;
using osuTK;

namespace JukeBox.Game.Tests.Charts
{
    [TestFixture]
    public class ModeChartComputationsTest
    {
        // ---- Parser: mania hold notes ----------------------------------------------------------

        [Test]
        public void HoldNoteEndTimeIsParsed()
        {
            var beatmap = BeatmapParser.ParseLines(new[]
            {
                "[HitObjects]",
                "448,192,1000,128,0,2500:0:0:0:0:",
            });

            Assert.That(beatmap.HitObjects, Has.Count.EqualTo(1));

            var hold = beatmap.HitObjects[0];
            Assert.That(hold.Kind, Is.EqualTo(HitObjectKind.Hold));
            Assert.That(hold.Time, Is.EqualTo(1000));
            Assert.That(hold.EndTime, Is.EqualTo(2500));
        }

        [Test]
        public void MalformedHoldIsSkippedNotFatal()
        {
            var beatmap = BeatmapParser.ParseLines(new[]
            {
                "[HitObjects]",
                "448,192,1000,128,0,notanumber:0:0",
                "448,192,1000,128,0", // missing params entirely
                "64,192,2000,1,0",    // valid circle survives
            });

            Assert.That(beatmap.HitObjects, Has.Count.EqualTo(1));
            Assert.That(beatmap.HitObjects[0].Kind, Is.EqualTo(HitObjectKind.Circle));
        }

        [Test]
        public void HoldEndTimeBeforeStartIsClampedToStart()
        {
            var beatmap = BeatmapParser.ParseLines(new[]
            {
                "[HitObjects]",
                "448,192,1000,128,0,400:0:0:0:0:",
            });

            Assert.That(beatmap.HitObjects[0].EndTime, Is.EqualTo(1000));
        }

        [Test]
        public void HoldContributesPressAndReleaseHitsoundEvents()
        {
            var beatmap = BeatmapParser.ParseLines(new[]
            {
                "[TimingPoints]", "0,500,4,1,0,100,1,0",
                "[HitObjects]", "448,192,1000,128,0,2500:0:0:0:0:",
            });

            var player = new HitSoundPlayer(beatmap, new JukeBox.Game.Beatmaps.CachedBeatmapSet());
            Assert.That(player.EventCount, Is.EqualTo(2));
        }

        // ---- Mania lane mapping ----------------------------------------------------------------

        [Test]
        public void ManiaKeyCountComesFromCircleSize()
        {
            Assert.That(ModeChartComputations.ManiaKeyCount(4), Is.EqualTo(4));
            Assert.That(ModeChartComputations.ManiaKeyCount(7.2f), Is.EqualTo(7));
            Assert.That(ModeChartComputations.ManiaKeyCount(0), Is.EqualTo(1));   // clamped
            Assert.That(ModeChartComputations.ManiaKeyCount(99), Is.EqualTo(18)); // clamped
        }

        [Test]
        public void ManiaLaneMappingCoversTheEdges()
        {
            // 4K: lane boundaries at 128/256/384.
            Assert.That(ModeChartComputations.ManiaLane(0, 4), Is.EqualTo(0));
            Assert.That(ModeChartComputations.ManiaLane(127.9f, 4), Is.EqualTo(0));
            Assert.That(ModeChartComputations.ManiaLane(128, 4), Is.EqualTo(1));
            Assert.That(ModeChartComputations.ManiaLane(511, 4), Is.EqualTo(3));
            Assert.That(ModeChartComputations.ManiaLane(512, 4), Is.EqualTo(3)); // clamped
            Assert.That(ModeChartComputations.ManiaLane(-5, 4), Is.EqualTo(0)); // clamped
        }

        [Test]
        public void SpecialLaneOnlyExistsForOddKeyCounts()
        {
            Assert.That(ModeChartComputations.IsSpecialLane(3, 7), Is.True);
            Assert.That(ModeChartComputations.IsSpecialLane(2, 7), Is.False);
            Assert.That(ModeChartComputations.IsSpecialLane(2, 4), Is.False);
        }

        // ---- Taiko classification --------------------------------------------------------------

        [Test]
        public void KatDonAndBigClassification()
        {
            Assert.That(ModeChartComputations.IsKat(0), Is.False);  // don
            Assert.That(ModeChartComputations.IsKat(2), Is.True);   // whistle → kat
            Assert.That(ModeChartComputations.IsKat(8), Is.True);   // clap → kat
            Assert.That(ModeChartComputations.IsKat(4), Is.False);  // finish alone is a big don

            Assert.That(ModeChartComputations.IsBig(4), Is.True);
            Assert.That(ModeChartComputations.IsBig(2 | 4), Is.True); // big kat
            Assert.That(ModeChartComputations.IsBig(2), Is.False);
        }

        // ---- Catch plate / droplets / bananas --------------------------------------------------

        [Test]
        public void PlateKeyframesArriveAtTargetsInTimeOrder()
        {
            var keyframes = ModeChartComputations.PlateKeyframes(new[]
            {
                new CatchDropSpec(2000, 400),
                new CatchDropSpec(1000, 100),
                new CatchDropSpec(3000, 250),
            });

            Assert.That(keyframes.Select(k => k.Time), Is.EqualTo(new double[] { 1000, 2000, 3000 }));
            Assert.That(keyframes.Select(k => k.X), Is.EqualTo(new float[] { 100, 400, 250 }));
        }

        [Test]
        public void PlateKeyframesAreCappedUnderHostileCounts()
        {
            var targets = Enumerable.Range(0, 100_000).Select(i => new CatchDropSpec(i * 10, i % 512));
            var keyframes = ModeChartComputations.PlateKeyframes(targets);

            Assert.That(keyframes.Count, Is.LessThanOrEqualTo(ModeChartComputations.max_plate_keyframes));
        }

        [Test]
        public void BananasAreCappedAndDeterministic()
        {
            var spinner = new ChartHitObject { Kind = HitObjectKind.Spinner, Time = 1000, EndTime = 100_000_000 };

            var first = ModeChartComputations.Bananas(spinner);
            var second = ModeChartComputations.Bananas(spinner);

            Assert.That(first.Count, Is.EqualTo(ModeChartComputations.max_bananas_per_spinner));
            Assert.That(first, Is.EqualTo(second), "banana positions must be deterministic per spinner");
            Assert.That(first.All(b => b.X is >= 0 and <= 512), Is.True);
        }

        [Test]
        public void SliderDropletsAreCappedAndSpanTheDuration()
        {
            var slider = new ChartHitObject
            {
                Kind = HitObjectKind.Slider,
                X = 0, Y = 100, Time = 1000,
                CurveType = 'L', Slides = 1, PixelLength = 200, SpanDuration = 100_000,
                EndTime = 101_000,
                ControlPoints = { new Vector2(0, 100), new Vector2(200, 100) },
            };

            var droplets = ModeChartComputations.SliderDroplets(slider);

            Assert.That(droplets.Count, Is.LessThanOrEqualTo(ModeChartComputations.max_droplets_per_slider));
            Assert.That(droplets.First().Time, Is.EqualTo(1000));
            Assert.That(droplets.Last().Time, Is.EqualTo(101_000).Within(1e-6));
            Assert.That(droplets.First().X, Is.EqualTo(0f).Within(0.5f));
            Assert.That(droplets.Last().X, Is.EqualTo(200f).Within(0.5f));
        }

        [Test]
        public void CatchTargetsSkipNothingAndStaySorted()
        {
            var beatmap = BeatmapParser.ParseLines(new[]
            {
                "[Difficulty]", "SliderMultiplier:1.4",
                "[TimingPoints]", "0,500,4,1,0,100,1,0",
                "[HitObjects]",
                "100,192,3000,1,0",
                "50,192,1000,2,0,L|250:192,1,140",
                "256,192,5000,12,0,6000",
            });

            var targets = ModeChartComputations.CatchTargets(beatmap);

            Assert.That(targets.Count, Is.GreaterThan(3));
            Assert.That(targets, Is.Ordered.By(nameof(CatchDropSpec.Time)));
        }
    }
}
