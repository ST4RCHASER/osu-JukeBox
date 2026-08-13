#nullable enable

using System;
using System.Linq;
using JukeBox.Game.Charts;
using NUnit.Framework;
using osu.Framework.Testing;
using osu.Framework.Timing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// ManualClock coverage for the three mode-specific chart renderers: the core autoplay
    /// contract of each (note at judgement line / target / plate under fruit exactly on its
    /// time), lifetime windows, and hostile-input load bounds.
    /// </summary>
    [TestFixture]
    public partial class TestSceneModeChartLayers : JukeBoxTestScene
    {
        private readonly ManualClock manual = new ManualClock();

        private FramedClock clock() => new FramedClock(manual);

        // ---- Mania -----------------------------------------------------------------------------

        private static readonly string[] mania_fixture =
        {
            "[Difficulty]", "CircleSize:4",
            "[TimingPoints]", "0,500,4,1,0,100,1,0",
            "[HitObjects]",
            "64,192,5000,1,0",                 // lane 0 note at t=5000
            "448,192,8000,128,0,9000:0:0:0:0:", // lane 3 hold 8000..9000
        };

        [Test]
        public void ManiaNoteReachesJudgementLineAtItsTime()
        {
            ManiaChartLayer layer = null!;

            AddStep("create mania layer", () =>
            {
                manual.CurrentTime = 0;
                Child = layer = new ManiaChartLayer(BeatmapParser.ParseLines(mania_fixture)) { Clock = clock() };
            });

            AddUntilStep("layer loaded", () => layer.IsLoaded);
            AddAssert("4 keys", () => layer.KeyCount == 4);
            AddAssert("both objects compiled", () => layer.TotalObjectCount == 2);
            AddAssert("nothing alive at t=0", () => layer.AliveObjectCount == 0);

            AddStep("t=5000 (note hit time)", () => manual.CurrentTime = 5000);
            AddUntilStep("note alive", () => layer.AliveObjectCount == 1);
            AddUntilStep("note bottom edge sits on the judgement line", () =>
            {
                var note = layer.ChildrenOfType<ManiaChartLayer.DrawableManiaNote>().Single();
                return Math.Abs(note.Y - ManiaChartLayer.judgement_line_y) < 1;
            });

            AddStep("t=8500 (mid-hold)", () => manual.CurrentTime = 8500);
            AddUntilStep("hold alive and pinned to the line", () =>
            {
                var hold = layer.ChildrenOfType<ManiaChartLayer.DrawableManiaHold>().Single();
                return Math.Abs(hold.Y - ManiaChartLayer.judgement_line_y) < 1 && hold.Alpha > 0;
            });

            AddStep("t=10000 (all done)", () => manual.CurrentTime = 10000);
            AddUntilStep("nothing alive", () => layer.AliveObjectCount == 0);

            AddStep("seek back to t=5000", () => manual.CurrentTime = 5000);
            AddUntilStep("note revived", () => layer.AliveObjectCount == 1);
        }

        [Test]
        public void ManiaHostileHoldDurationStillLoads()
        {
            ManiaChartLayer layer = null!;

            AddStep("create 10^8-ms hold", () =>
            {
                manual.CurrentTime = 0;
                Child = layer = new ManiaChartLayer(BeatmapParser.ParseLines(new[]
                {
                    "[Difficulty]", "CircleSize:4",
                    "[HitObjects]", "64,192,1000,128,0,100000000:0:0:0:0:",
                })) { Clock = clock() };
            });

            AddUntilStep("layer loaded", () => layer.IsLoaded);
            AddAssert("hold compiled", () => layer.TotalObjectCount == 1);
        }

        // ---- Taiko -----------------------------------------------------------------------------

        private static readonly string[] taiko_fixture =
        {
            "[Difficulty]", "SliderMultiplier:1.4",
            "[TimingPoints]", "0,500,4,1,0,100,1,0",
            "[HitObjects]",
            "256,192,5000,1,0",              // don at 5000
            "256,192,6000,1,8",              // kat (clap) at 6000
            "0,192,8000,2,0,L|140:192,1,140", // drumroll 8000..8500
            "256,192,10000,12,0,11000",      // denden 10000..11000
        };

        [Test]
        public void TaikoNoteReachesTargetAtItsTime()
        {
            TaikoChartLayer layer = null!;

            AddStep("create taiko layer", () =>
            {
                manual.CurrentTime = 0;
                Child = layer = new TaikoChartLayer(BeatmapParser.ParseLines(taiko_fixture)) { Clock = clock() };
            });

            AddUntilStep("layer loaded", () => layer.IsLoaded);
            AddAssert("4 objects compiled", () => layer.TotalObjectCount == 4);

            AddStep("t=5000 (don hit time)", () => manual.CurrentTime = 5000);
            AddUntilStep("don sits on the target", () =>
            {
                var hit = layer.ChildrenOfType<TaikoChartLayer.DrawableTaikoHit>().First();
                return Math.Abs(hit.X - TaikoChartLayer.target_x) < 1 && hit.Alpha > 0;
            });

            AddStep("t=10500 (mid-spin)", () => manual.CurrentTime = 10500);
            AddUntilStep("denden alive at target", () => layer.AliveObjectCount == 1);

            AddStep("t=12000 (all done)", () => manual.CurrentTime = 12000);
            AddUntilStep("nothing alive", () => layer.AliveObjectCount == 0);
        }

        // ---- Catch -----------------------------------------------------------------------------

        private static readonly string[] catch_fixture =
        {
            "[Difficulty]", "ApproachRate:5", "SliderMultiplier:1.4",
            "[TimingPoints]", "0,500,4,1,0,100,1,0",
            "[HitObjects]",
            "100,192,5000,1,0",  // fruit at x=100, t=5000
            "400,192,7000,1,0",  // fruit at x=400, t=7000
        };

        [Test]
        public void CatchPlateIsUnderEachFruitAtItsTime()
        {
            CatchChartLayer layer = null!;

            AddStep("create catch layer", () =>
            {
                manual.CurrentTime = 0;
                Child = layer = new CatchChartLayer(BeatmapParser.ParseLines(catch_fixture)) { Clock = clock() };
            });

            AddUntilStep("layer loaded", () => layer.IsLoaded);
            AddAssert("2 fruits compiled", () => layer.TotalObjectCount == 2);

            AddStep("t=5000 (first fruit)", () => manual.CurrentTime = 5000);
            AddUntilStep("fruit fell to the catch line", () =>
            {
                var fruit = layer.ChildrenOfType<CatchChartLayer.DrawableFallingCatch>().First();
                return Math.Abs(fruit.Y - CatchChartLayer.catch_y) < 1;
            });
            AddUntilStep("plate under first fruit", () => Math.Abs(layer.Plate.X - 100) < 1);

            AddStep("t=7000 (second fruit)", () => manual.CurrentTime = 7000);
            AddUntilStep("plate under second fruit", () => Math.Abs(layer.Plate.X - 400) < 1);

            AddStep("t=8000 (all done)", () => manual.CurrentTime = 8000);
            AddUntilStep("nothing alive", () => layer.AliveObjectCount == 0);
        }

        [Test]
        public void CatchHostileSpinnerLoadsWithCappedBananas()
        {
            CatchChartLayer layer = null!;

            AddStep("create 10^8-ms banana shower", () =>
            {
                manual.CurrentTime = 0;
                Child = layer = new CatchChartLayer(BeatmapParser.ParseLines(new[]
                {
                    "[Difficulty]", "ApproachRate:5",
                    "[HitObjects]", "256,192,1000,12,0,100000000",
                })) { Clock = clock() };
            });

            AddUntilStep("layer loaded", () => layer.IsLoaded);
            AddAssert("banana count capped", () => layer.TotalObjectCount <= ModeChartComputations.max_bananas_per_spinner);
        }
    }
}
