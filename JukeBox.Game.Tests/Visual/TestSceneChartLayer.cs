#nullable enable

using JukeBox.Game.Charts;
using NUnit.Framework;
using osu.Framework.Testing;
using osu.Framework.Timing;

namespace JukeBox.Game.Tests.Visual
{
    [TestFixture]
    public partial class TestSceneChartLayer : JukeBoxTestScene
    {
        private readonly ManualClock manual = new ManualClock();

        private ChartLayer layer = null!;

        // AR5 → preempt 1200ms. Circle at t=5000 (window 3800..~5200), slider at t=8000,
        // spinner at t=12000..13000.
        private static readonly string[] fixture = """
            [Difficulty]
            CircleSize:4
            ApproachRate:5
            SliderMultiplier:1.4

            [TimingPoints]
            0,500,4,1,0,100,1,0

            [HitObjects]
            256,192,5000,1,0
            100,100,8000,6,0,B|200:100|300:100,1,140
            256,192,12000,12,0,13000
            """.Split('\n');

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create layer", () =>
            {
                manual.CurrentTime = 0;

                // Clock assigned BEFORE attaching, same as TestSceneStoryboardLayer: the layer
                // compiles its transforms at load against this clock.
                Child = layer = new ChartLayer(BeatmapParser.ParseLines(fixture))
                {
                    Clock = new FramedClock(manual),
                };
            });

            AddUntilStep("layer loaded", () => layer.IsLoaded);
            AddAssert("three objects compiled", () => layer.TotalObjectCount == 3);
        }

        [Test]
        public void ObjectAliveOnlyWithinPreemptWindow()
        {
            AddAssert("nothing alive at t=0", () => layer.AliveObjectCount == 0);

            AddStep("t=4500 (inside circle preempt)", () => manual.CurrentTime = 4500);
            AddUntilStep("circle alive", () => layer.AliveObjectCount == 1);

            AddStep("t=6000 (past circle fade-out)", () => manual.CurrentTime = 6000);
            AddUntilStep("circle gone", () => layer.AliveObjectCount == 0);
        }

        [Test]
        public void SeekBackwardsRevivesObject()
        {
            AddStep("t=4500", () => manual.CurrentTime = 4500);
            AddUntilStep("circle alive", () => layer.AliveObjectCount == 1);

            AddStep("t=6000", () => manual.CurrentTime = 6000);
            AddUntilStep("circle gone", () => layer.AliveObjectCount == 0);

            AddStep("seek back to t=4500", () => manual.CurrentTime = 4500);
            AddUntilStep("circle alive again", () => layer.AliveObjectCount == 1);
        }

        [Test]
        public void SliderStaysAliveUntilItsEndTime()
        {
            // Slider: 140px at SV 1 → 500ms span, so alive from 6800 to ~8550.
            AddStep("t=8400 (slider still travelling)", () => manual.CurrentTime = 8400);
            AddUntilStep("slider alive", () => layer.AliveObjectCount == 1);

            AddStep("t=9000 (past slider end + fade)", () => manual.CurrentTime = 9000);
            AddUntilStep("slider gone", () => layer.AliveObjectCount == 0);
        }

        [Test]
        public void SpinnerAliveForItsWholeDuration()
        {
            AddStep("t=12500 (mid-spin)", () => manual.CurrentTime = 12500);
            AddUntilStep("spinner alive", () => layer.AliveObjectCount == 1);

            AddStep("t=13500 (past end + fade)", () => manual.CurrentTime = 13500);
            AddUntilStep("spinner gone", () => layer.AliveObjectCount == 0);
        }
    }
}
