#nullable enable

using System;
using System.IO;
using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Charts;
using NUnit.Framework;
using osuTK;

namespace JukeBox.Game.Tests.Charts
{
    [TestFixture]
    public class BeatmapParserTest
    {
        private static readonly string[] fixture = """
            osu file format v14

            [General]
            AudioFilename: audio.mp3
            Mode: 0

            [Metadata]
            Title:Test Song
            Version:Insane

            [Difficulty]
            HPDrainRate:5
            CircleSize:4
            OverallDifficulty:7
            ApproachRate:9
            SliderMultiplier:1.4
            SliderTickRate:2

            [TimingPoints]
            1000,500,4,2,1,60,1,0
            2000,-50,4,2,1,80,0,0

            [HitObjects]
            256,192,1000,1,0,0:0:0:0:
            100,100,2000,2,0,B|200:100|300:100,2,140,2|0|8,0:0|0:0|0:0,0:0:0:0:
            256,192,4000,12,0,6000,0:0:0:0:
            garbage line
            1,2
            50,50,7000,5,0
            """.Split('\n');

        private ChartBeatmap parse() => BeatmapParser.ParseLines(fixture);

        [Test]
        public void DifficultyValuesAreParsed()
        {
            var beatmap = parse();

            Assert.That(beatmap.CircleSize, Is.EqualTo(4f));
            Assert.That(beatmap.ApproachRate, Is.EqualTo(9f));
            Assert.That(beatmap.SliderMultiplier, Is.EqualTo(1.4));
            Assert.That(beatmap.SliderTickRate, Is.EqualTo(2.0));

            // Derived display values: r = 54.4 − 4.48·CS; AR9 preempt = 1200 − 750·(9−5)/5 = 600.
            Assert.That(beatmap.CircleRadius, Is.EqualTo(54.4f - 4.48f * 4).Within(0.001f));
            Assert.That(beatmap.PreemptMs, Is.EqualTo(600).Within(0.001));
            Assert.That(beatmap.FadeInMs, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void ApproachRateFallsBackToOverallDifficulty()
        {
            var beatmap = BeatmapParser.ParseLines(new[]
            {
                "[Difficulty]",
                "OverallDifficulty:3",
            });

            Assert.That(beatmap.ApproachRate, Is.EqualTo(3f));
        }

        [Test]
        public void TimingPointsAreParsed()
        {
            var beatmap = parse();

            Assert.That(beatmap.TimingPoints, Has.Count.EqualTo(2));

            var uninherited = beatmap.TimingPoints[0];
            Assert.That(uninherited.Uninherited, Is.True);
            Assert.That(uninherited.BeatLength, Is.EqualTo(500));
            Assert.That(uninherited.SampleSet, Is.EqualTo(2));
            Assert.That(uninherited.SampleIndex, Is.EqualTo(1));
            Assert.That(uninherited.Volume, Is.EqualTo(60));

            var inherited = beatmap.TimingPoints[1];
            Assert.That(inherited.Uninherited, Is.False);
            Assert.That(inherited.SvMultiplier, Is.EqualTo(2.0).Within(1e-9)); // -100 / -50
        }

        [Test]
        public void TimingLookupsResolveByTime()
        {
            var beatmap = parse();

            Assert.That(beatmap.BeatLengthAt(1500), Is.EqualTo(500));
            Assert.That(beatmap.BeatLengthAt(0), Is.EqualTo(500));      // before first point → first uninherited
            Assert.That(beatmap.SliderVelocityAt(1500), Is.EqualTo(1)); // before the inherited point
            Assert.That(beatmap.SliderVelocityAt(2500), Is.EqualTo(2));
            Assert.That(beatmap.SamplePointAt(2500)!.Volume, Is.EqualTo(80));
        }

        [Test]
        public void MalformedLinesAreSkippedNotFatal()
        {
            var beatmap = parse();

            // "garbage line" and "1,2" must be dropped; the valid objects all survive.
            Assert.That(beatmap.HitObjects, Has.Count.EqualTo(4));
        }

        [Test]
        public void CircleIsParsed()
        {
            var circle = parse().HitObjects[0];

            Assert.That(circle.Kind, Is.EqualTo(HitObjectKind.Circle));
            Assert.That(circle.X, Is.EqualTo(256f));
            Assert.That(circle.Y, Is.EqualTo(192f));
            Assert.That(circle.Time, Is.EqualTo(1000));
            Assert.That(circle.EndTime, Is.EqualTo(1000));
            Assert.That(circle.NewCombo, Is.False);
        }

        [Test]
        public void SliderIsParsedWithSvAdjustedDuration()
        {
            var slider = parse().HitObjects[1];

            Assert.That(slider.Kind, Is.EqualTo(HitObjectKind.Slider));
            Assert.That(slider.CurveType, Is.EqualTo('B'));
            Assert.That(slider.ControlPoints, Is.EqualTo(new[]
            {
                new Vector2(100, 100), new Vector2(200, 100), new Vector2(300, 100),
            }));
            Assert.That(slider.Slides, Is.EqualTo(2));
            Assert.That(slider.PixelLength, Is.EqualTo(140));
            Assert.That(slider.EdgeSounds, Is.EqualTo(new[] { 2, 0, 8 }));

            // At t=2000 the SV multiplier is 2: one span = 140 / (1.4 · 100 · 2) · 500 = 250ms,
            // two slides → 500ms total.
            Assert.That(slider.SpanDuration, Is.EqualTo(250).Within(1e-6));
            Assert.That(slider.EndTime, Is.EqualTo(2500).Within(1e-6));
        }

        [Test]
        public void SpinnerIsParsedWithEndTime()
        {
            var spinner = parse().HitObjects[2];

            Assert.That(spinner.Kind, Is.EqualTo(HitObjectKind.Spinner));
            Assert.That(spinner.Time, Is.EqualTo(4000));
            Assert.That(spinner.EndTime, Is.EqualTo(6000));
        }

        [Test]
        public void NewComboBitIsParsed()
        {
            Assert.That(parse().HitObjects[3].NewCombo, Is.True); // type 5 = circle | new-combo
        }

        [Test]
        public void HitSoundEventsCoverHeadRepeatsAndTail()
        {
            var beatmap = parse();
            var player = new HitSoundPlayer(beatmap, new CachedBeatmapSet());

            // circle (1) + slider head/repeat/tail (3) + spinner end (1) + circle (1)
            Assert.That(player.EventCount, Is.EqualTo(6));
        }

        [Test]
        public void SliderCurveSamplingTrimsToPixelLength()
        {
            var points = new[] { new Vector2(0, 0), new Vector2(100, 0), new Vector2(200, 0) };
            var path = SliderCurve.Sample('L', points, 150);

            double length = 0;
            for (int i = 1; i < path.Count; i++)
                length += (path[i] - path[i - 1]).Length;

            Assert.That(length, Is.EqualTo(150).Within(0.5));
            Assert.That(path[^1].X, Is.EqualTo(150f).Within(0.5f));
        }

        [Test]
        public void PerfectCurvePassesThroughAllControlPoints()
        {
            // Quarter-ish arc: (0,0) → (50,50) → (100,0) lie on a circle centred (50,0), r=50.
            var points = new[] { new Vector2(0, 0), new Vector2(50, 50), new Vector2(100, 0) };
            var path = SliderCurve.Sample('P', points, 1000); // generous length: no trimming

            Assert.That(path.Any(p => (p - new Vector2(50, 50)).Length < 2), Is.True,
                "arc must pass through the middle control point");
            Assert.That(path[0].Length, Is.LessThan(0.01f), "arc must start at the head");
        }

        [Test]
        public void ScannerReadsVersionFromMetadata()
        {
            string tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            try
            {
                string osu = Path.Combine(tmp, "a.osu");
                File.WriteAllText(osu,
                    "osu file format v14\n\n[General]\nAudioFilename: audio.mp3\nMode: 1\n\n[Metadata]\nTitle:x\nVersion:Hard\n\n[Events]\n");

                var info = OsuFileScanner.Scan(osu);
                Assert.That(info.Version, Is.EqualTo("Hard"));
                Assert.That(info.Mode, Is.EqualTo(1));
            }
            finally
            {
                Directory.Delete(tmp, true);
            }
        }

        [Test]
        public void CacheLoadBuildsDifficultyList()
        {
            string tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            try
            {
                // Alphabetical scan order puts the taiko diff first — the std diff must still win
                // PreferredOsuFile, and both must be listed with their versions/modes.
                File.WriteAllText(Path.Combine(tmp, "a_taiko.osu"),
                    "osu file format v14\n\n[General]\nAudioFilename: audio.mp3\nMode: 1\n\n[Metadata]\nVersion:Oni\n\n[Events]\n");
                File.WriteAllText(Path.Combine(tmp, "b_std.osu"),
                    "osu file format v14\n\n[General]\nAudioFilename: audio.mp3\nMode: 0\n\n[Metadata]\nVersion:Insane\n\n[Events]\n");

                var cache = new BeatmapCache(Path.Combine(tmp, "unused-cache"), new JukeBox.Game.Tests.Beatmaps.FileMirror(Path.Combine(tmp, "unused.osz")));
                var set = cache.LoadFromDirectory(1, tmp);

                Assert.That(set.Difficulties, Has.Count.EqualTo(2));
                Assert.That(set.Difficulties[0].Version, Is.EqualTo("Oni"));
                Assert.That(set.Difficulties[0].Mode, Is.EqualTo(1));
                Assert.That(set.Difficulties[1].Version, Is.EqualTo("Insane"));
                Assert.That(set.Difficulties[1].Mode, Is.EqualTo(0));
                Assert.That(set.Difficulties[1].AudioFilename, Is.EqualTo("audio.mp3"));
                Assert.That(set.PreferredOsuFile, Does.EndWith("b_std.osu"));
            }
            finally
            {
                Directory.Delete(tmp, true);
            }
        }
    }
}
