#nullable enable

using System;
using System.IO;
using System.Linq;
using JukeBox.Game.Charts;
using NUnit.Framework;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osuTK;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Regression coverage for the "slider ball never visible" report: drives a REAL beatmap
    /// (committed fixture from the osu! catalogue, 24 sliders) through <see cref="ChartLayer"/>
    /// on a manual clock and asserts the ball is actually visible and travelling mid-slider.
    /// </summary>
    [TestFixture]
    public partial class TestSceneSliderBall : JukeBoxTestScene
    {
        private readonly ManualClock manual = new ManualClock();

        private ChartLayer layer = null!;
        private ChartBeatmap beatmap = null!;
        private ChartHitObject slider = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create layer from real map", () =>
            {
                beatmap = BeatmapParser.Parse(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "happy_people_easy.osu"));
                slider = beatmap.HitObjects.First(o => o.Kind == HitObjectKind.Slider);

                manual.CurrentTime = 0;
                Child = layer = new ChartLayer(beatmap)
                {
                    Clock = new FramedClock(manual),
                };
            });

            AddUntilStep("layer loaded", () => layer.IsLoaded);
            AddAssert("fixture really has sliders", () => beatmap.HitObjects.Count(o => o.Kind == HitObjectKind.Slider) > 10);
        }

        private ChartLayer.DrawableChartSlider sliderDrawable()
            => layer.ChildrenOfType<ChartLayer.DrawableChartSlider>().First(d => d.LifetimeStart < slider.Time && slider.Time < d.LifetimeEnd);

        [Test]
        public void BallIsVisibleAndTravellingMidSlider()
        {
            double mid = (slider.Time + slider.EndTime) / 2;

            Vector2 startPosition = default;
            AddStep("capture ball start position", () => startPosition = sliderDrawable().BallContainer.Position);

            // The original bug: transforms compiled before the ball had a clock were applied
            // instantly-to-end-state and discarded — the ball ended at Alpha 0 with ZERO
            // registered transforms. Guard the mechanism itself, not just the symptom.
            AddAssert("ball transforms registered", () => sliderDrawable().BallContainer.Transforms.Any());
            AddAssert("first ball transform starts at slider time",
                () => sliderDrawable().BallContainer.Transforms.Min(t => t.StartTime) == slider.Time);

            AddStep("seek to mid-slider", () => manual.CurrentTime = mid);
            AddUntilStep("slider drawable alive", () => layer.AliveObjectCount >= 1);

            AddUntilStep("ball is visible mid-slider", () => sliderDrawable().BallContainer.Alpha > 0);
            AddUntilStep("ball has moved from the head", () => (sliderDrawable().BallContainer.Position - startPosition).Length > 5);
        }
    }
}
