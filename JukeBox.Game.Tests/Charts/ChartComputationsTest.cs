#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Charts;
using NUnit.Framework;
using osuTK;

namespace JukeBox.Game.Tests.Charts
{
    [TestFixture]
    public class ChartComputationsTest
    {
        private static ChartHitObject circle(float x, float y, double time, bool newCombo = false) => new ChartHitObject
        {
            X = x, Y = y, Time = time, EndTime = time, Kind = HitObjectKind.Circle, NewCombo = newCombo,
        };

        // ---- Combo assignment ------------------------------------------------------------------

        [Test]
        public void ComboNumbersResetOnNewComboAndColoursCycle()
        {
            var objects = new List<ChartHitObject>
            {
                circle(0, 0, 1000),                       // implicit first combo
                circle(50, 0, 1500),
                new ChartHitObject { Kind = HitObjectKind.Slider, Time = 2000, EndTime = 2500, Slides = 1 },
                circle(100, 0, 3000, newCombo: true),     // combo 2
                new ChartHitObject { Kind = HitObjectKind.Spinner, Time = 3500, EndTime = 4000 },
                circle(150, 0, 4500),                     // spinner must not have reset numbering
                circle(200, 0, 5000, newCombo: true),     // combo 3
            };

            var combos = ChartComputations.AssignCombos(objects, 4);

            Assert.That(combos.Select(c => c.NumberInCombo), Is.EqualTo(new[] { 1, 2, 3, 1, 0, 2, 1 }));
            Assert.That(combos.Select(c => c.ColourIndex), Is.EqualTo(new[] { 0, 0, 0, 1, 1, 1, 2 }));
        }

        [Test]
        public void ComboColoursWrapAroundThePalette()
        {
            var objects = Enumerable.Range(0, 6)
                                    .Select(i => circle(i * 10, 0, i * 1000, newCombo: true))
                                    .ToList();

            var combos = ChartComputations.AssignCombos(objects, 4);

            Assert.That(combos.Select(c => c.ColourIndex), Is.EqualTo(new[] { 0, 1, 2, 3, 0, 1 }));
        }

        // ---- Stacking --------------------------------------------------------------------------

        private static ChartBeatmap makeBeatmap(params ChartHitObject[] objects) => new ChartBeatmap
        {
            ApproachRate = 5, // preempt 1200; leniency 0.7 → stack threshold 840ms
            HitObjects = objects.ToList(),
        };

        [Test]
        public void PerfectStackAssignsDescendingHeights()
        {
            var beatmap = makeBeatmap(
                circle(256, 192, 1000),
                circle(256, 192, 1300),
                circle(256, 192, 1600));

            ChartComputations.ApplyStacking(beatmap);

            // Earliest object sits highest on the stack (shifted furthest up-left), the last one
            // stays at its true position — the classic stable behaviour.
            Assert.That(beatmap.HitObjects.Select(o => o.StackHeight), Is.EqualTo(new[] { 2, 1, 0 }));
        }

        [Test]
        public void ObjectsOutsideTheTimeThresholdDoNotStack()
        {
            var beatmap = makeBeatmap(
                circle(256, 192, 1000),
                circle(256, 192, 5000)); // 4s apart >> 840ms threshold

            ChartComputations.ApplyStacking(beatmap);

            Assert.That(beatmap.HitObjects.Select(o => o.StackHeight), Is.EqualTo(new[] { 0, 0 }));
        }

        [Test]
        public void ObjectsFarApartInSpaceDoNotStack()
        {
            var beatmap = makeBeatmap(
                circle(100, 100, 1000),
                circle(300, 100, 1300));

            ChartComputations.ApplyStacking(beatmap);

            Assert.That(beatmap.HitObjects.Select(o => o.StackHeight), Is.EqualTo(new[] { 0, 0 }));
        }

        [Test]
        public void CircleOnSliderEndStacksDownward()
        {
            // A 100px straight slider ending at (200, 100), followed by circles on its end point.
            var slider = new ChartHitObject
            {
                X = 100, Y = 100, Time = 1000, EndTime = 1500,
                Kind = HitObjectKind.Slider, CurveType = 'L', Slides = 1, PixelLength = 100,
                SpanDuration = 500,
                ControlPoints = { new Vector2(100, 100), new Vector2(200, 100) },
            };

            var beatmap = makeBeatmap(
                slider,
                circle(200, 100, 1800),
                circle(200, 100, 2100));

            ChartComputations.ApplyStacking(beatmap);

            // Stacked under the slider's end: negative heights, later circle lower.
            Assert.That(beatmap.HitObjects[0].StackHeight, Is.EqualTo(0));
            Assert.That(beatmap.HitObjects[1].StackHeight, Is.EqualTo(-1));
            Assert.That(beatmap.HitObjects[2].StackHeight, Is.EqualTo(-2));
        }

        [Test]
        public void StackOffsetScalesWithRadius()
        {
            var obj = circle(0, 0, 0);
            obj.StackHeight = 2;

            var offset = ChartComputations.StackOffset(obj, 32);

            // 2 layers · −6.4 · (32/64) = −6.4 on both axes.
            Assert.That(offset.X, Is.EqualTo(-6.4f).Within(1e-4f));
            Assert.That(offset.Y, Is.EqualTo(-6.4f).Within(1e-4f));
        }

        [Test]
        public void StackingIsIdempotent()
        {
            var beatmap = makeBeatmap(
                circle(256, 192, 1000),
                circle(256, 192, 1300));

            ChartComputations.ApplyStacking(beatmap);
            ChartComputations.ApplyStacking(beatmap); // e.g. chart layer re-created on toggle

            Assert.That(beatmap.HitObjects.Select(o => o.StackHeight), Is.EqualTo(new[] { 1, 0 }));
        }

        // ---- Slider ticks ----------------------------------------------------------------------

        private static ChartHitObject makeSlider(double time, int slides, double spanDuration, float lengthPx)
        {
            var slider = new ChartHitObject
            {
                X = 0, Y = 0, Time = time,
                Kind = HitObjectKind.Slider, CurveType = 'L',
                Slides = slides, PixelLength = lengthPx, SpanDuration = spanDuration,
                EndTime = time + spanDuration * slides,
                ControlPoints = { new Vector2(0, 0), new Vector2(lengthPx, 0) },
            };
            return slider;
        }

        private static readonly List<Vector2> straight_path_200 = new List<Vector2> { new Vector2(0, 0), new Vector2(200, 0) };

        [Test]
        public void TickTimesFollowBeatLengthAndTickRate()
        {
            var beatmap = new ChartBeatmap
            {
                SliderTickRate = 2,
                TimingPoints = { new ChartTimingPoint { Time = 0, BeatLength = 500, Uninherited = true } },
            };

            // Span 1000ms, tick interval 500/2 = 250ms → ticks at +250/+500/+750 within the span.
            var slider = makeSlider(1000, 1, 1000, 200);
            var ticks = ChartComputations.SliderTicks(slider, beatmap, straight_path_200, 600);

            Assert.That(ticks.Select(t => t.Time), Is.EqualTo(new[] { 1250.0, 1500.0, 1750.0 }));
            Assert.That(ticks[0].Position.X, Is.EqualTo(50f).Within(0.5f));
            Assert.That(ticks[1].Position.X, Is.EqualTo(100f).Within(0.5f));

            // First-span ticks appear with the body (preempt before the head).
            Assert.That(ticks.All(t => t.AppearTime == 400), Is.True);
        }

        [Test]
        public void RepeatSpansPassTicksInReverseOrder()
        {
            var beatmap = new ChartBeatmap
            {
                SliderTickRate = 1,
                TimingPoints = { new ChartTimingPoint { Time = 0, BeatLength = 400, Uninherited = true } },
            };

            // Span 1000ms, interval 400 → forward ticks at +400 (80px) and +800 (160px). On the
            // reverse span the 160px tick is passed FIRST (at +1200) and the 80px tick second.
            var slider = makeSlider(0, 2, 1000, 200);
            var ticks = ChartComputations.SliderTicks(slider, beatmap, straight_path_200, 600);

            Assert.That(ticks.Select(t => t.Time), Is.EqualTo(new[] { 400.0, 800.0, 1200.0, 1600.0 }));
            Assert.That(ticks[2].Position.X, Is.EqualTo(160f).Within(0.5f));
            Assert.That(ticks[3].Position.X, Is.EqualTo(80f).Within(0.5f));

            // Second-span ticks appear at their span start, not with the body.
            Assert.That(ticks[2].AppearTime, Is.EqualTo(1000));
        }

        [Test]
        public void TicksNearTheEndsAreDropped()
        {
            var beatmap = new ChartBeatmap
            {
                SliderTickRate = 4,
                TimingPoints = { new ChartTimingPoint { Time = 0, BeatLength = 400, Uninherited = true } },
            };

            // 20px slider, interval 100ms over a 400ms span → tick distances 5/10/15px, all within
            // 10px of an end → every tick dropped.
            var shortPath = new List<Vector2> { new Vector2(0, 0), new Vector2(20, 0) };
            var slider = makeSlider(0, 1, 400, 20);

            var ticks = ChartComputations.SliderTicks(slider, beatmap, shortPath, 600);

            Assert.That(ticks, Is.Empty);
        }

        // ---- Reverse arrows --------------------------------------------------------------------

        [Test]
        public void ReverseArrowCountAndPlacementAlternate()
        {
            var slider = makeSlider(1000, 3, 500, 200);
            var arrows = ChartComputations.ReverseArrows(slider, straight_path_200, 600);

            Assert.That(arrows, Has.Count.EqualTo(2)); // slides − 1

            // First repeat: tail end, pointing back along the path (180°); appears with the body.
            Assert.That(arrows[0].Time, Is.EqualTo(1500));
            Assert.That(arrows[0].Position, Is.EqualTo(new Vector2(200, 0)));
            Assert.That(Math.Abs(((arrows[0].RotationDegrees % 360) + 360) % 360 - 180), Is.LessThan(1));
            Assert.That(arrows[0].AppearTime, Is.EqualTo(400)); // 1000 − preempt 600

            // Second repeat: head end, pointing forward (0°); appears when the previous span starts.
            Assert.That(arrows[1].Time, Is.EqualTo(2000));
            Assert.That(arrows[1].Position, Is.EqualTo(new Vector2(0, 0)));
            Assert.That(Math.Abs(((arrows[1].RotationDegrees % 360) + 360) % 360), Is.LessThan(1));
            Assert.That(arrows[1].AppearTime, Is.EqualTo(1500));
        }

        [Test]
        public void SingleSpanSliderHasNoArrows()
        {
            var slider = makeSlider(1000, 1, 500, 200);
            Assert.That(ChartComputations.ReverseArrows(slider, straight_path_200, 600), Is.Empty);
        }

        // ---- Ball keyframes --------------------------------------------------------------------

        [Test]
        public void BallKeyframesAreStrictlyMonotonicAcrossRepeats()
        {
            var slider = makeSlider(1000, 3, 600, 200);
            var keyframes = ChartComputations.BallKeyframes(slider, straight_path_200);

            Assert.That(keyframes.Count, Is.GreaterThan(3));

            for (int i = 1; i < keyframes.Count; i++)
                Assert.That(keyframes[i].Time, Is.GreaterThan(keyframes[i - 1].Time), $"keyframe {i} must be after {i - 1}");

            Assert.That(keyframes[0].Time, Is.EqualTo(1000));
            Assert.That(keyframes[^1].Time, Is.EqualTo(1000 + 3 * 600).Within(1e-6));
        }

        [Test]
        public void BallPingPongsBetweenHeadAndTail()
        {
            var slider = makeSlider(0, 2, 500, 200);
            var keyframes = ChartComputations.BallKeyframes(slider, straight_path_200);

            Assert.That(keyframes[0].Position, Is.EqualTo(new Vector2(0, 0)));

            // At the first span's end the ball is at the tail; by the second span's end, back home.
            var atBounce = keyframes.Single(k => Math.Abs(k.Time - 500) < 1e-6);
            Assert.That(atBounce.Position.X, Is.EqualTo(200f).Within(0.5f));
            Assert.That(keyframes[^1].Position.X, Is.EqualTo(0f).Within(0.5f));
        }

        // ---- Hostile repeat counts (MaxRenderedSlides clamp) -----------------------------------

        [Test]
        public void AbsurdSlideCountIsClampedEverywhereAndComputesPromptly()
        {
            var beatmap = new ChartBeatmap
            {
                SliderTickRate = 2,
                TimingPoints = { new ChartTimingPoint { Time = 0, BeatLength = 500, Uninherited = true } },
            };

            var slider = makeSlider(0, 100_000, 100, 200);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var arrows = ChartComputations.ReverseArrows(slider, straight_path_200, 600);
            var keyframes = ChartComputations.BallKeyframes(slider, straight_path_200);
            var ticks = ChartComputations.SliderTicks(slider, beatmap, straight_path_200, 600);
            stopwatch.Stop();

            Assert.That(arrows.Count, Is.EqualTo(ChartComputations.MaxRenderedSlides - 1));
            Assert.That(keyframes.Count, Is.LessThanOrEqualTo(601)); // 1 + ~600 bounded travel samples
            Assert.That(ticks.Count, Is.LessThanOrEqualTo(64));

            // The whole spec computation for a 100k-repeat slider must be effectively instant —
            // this is what stands between a hostile .osu and a 100k-drawable load.
            Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(2000));
        }

        [Test]
        public void SlideCountJustAboveTheClampBoundaryIsTruncated()
        {
            var justAbove = makeSlider(0, ChartComputations.MaxRenderedSlides + 1, 100, 200);
            var atLimit = makeSlider(0, ChartComputations.MaxRenderedSlides, 100, 200);
            var below = makeSlider(0, ChartComputations.MaxRenderedSlides - 1, 100, 200);

            Assert.That(ChartComputations.ReverseArrows(justAbove, straight_path_200, 600),
                Has.Count.EqualTo(ChartComputations.MaxRenderedSlides - 1));
            Assert.That(ChartComputations.ReverseArrows(atLimit, straight_path_200, 600),
                Has.Count.EqualTo(ChartComputations.MaxRenderedSlides - 1));
            Assert.That(ChartComputations.ReverseArrows(below, straight_path_200, 600),
                Has.Count.EqualTo(ChartComputations.MaxRenderedSlides - 2));

            // Keyframes stay bounded on either side of the boundary and remain monotonic.
            var keyframes = ChartComputations.BallKeyframes(justAbove, straight_path_200);
            Assert.That(keyframes.Count, Is.LessThanOrEqualTo(601));

            for (int i = 1; i < keyframes.Count; i++)
                Assert.That(keyframes[i].Time, Is.GreaterThan(keyframes[i - 1].Time));
        }

        // ---- Follow points ---------------------------------------------------------------------

        [Test]
        public void FollowPointsOnlyConnectWithinACombo()
        {
            var beatmap = makeBeatmap(
                circle(0, 100, 1000),
                circle(200, 100, 2000),                    // same combo → connected
                circle(400, 100, 3000, newCombo: true),    // new combo → NOT connected
                new ChartHitObject { Kind = HitObjectKind.Spinner, X = 256, Y = 192, Time = 3500, EndTime = 4000 },
                circle(100, 300, 4500));                   // after spinner → NOT connected

            var points = ChartComputations.FollowPoints(beatmap, 32);

            Assert.That(points, Is.Not.Empty);
            // All dots lie strictly on the A→B segment (y = 100, x between the padded ends).
            Assert.That(points.All(p => Math.Abs(p.Position.Y - 100) < 1e-3), Is.True);
            Assert.That(points.All(p => p.Position.X > 32 && p.Position.X < 200 - 32), Is.True);
            // And every dot is gone by B's hit time.
            Assert.That(points.All(p => p.DisappearTime == 2000), Is.True);
        }

        [Test]
        public void FollowPointsSkipTooShortGaps()
        {
            var beatmap = makeBeatmap(
                circle(100, 100, 1000),
                circle(140, 100, 1500)); // 40px apart < 2·padding for radius 32

            Assert.That(ChartComputations.FollowPoints(beatmap, 32), Is.Empty);
        }
    }
}
