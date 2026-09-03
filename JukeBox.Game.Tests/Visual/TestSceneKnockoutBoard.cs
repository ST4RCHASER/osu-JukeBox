#nullable enable

using System;
using System.Linq;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Replays;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Timing;
using osuTK.Graphics;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The knockout rail read straight from hand-made timelines, so the awkward moments — a fresh
    /// load where nobody has been simulated yet, a field half-simulated — are reproducible without a
    /// real recording racing the clock.
    /// </summary>
    [TestFixture]
    public partial class TestSceneKnockoutBoard : JukeBoxTestScene
    {
        private Container host = null!;
        private KnockoutBoard board = null!;
        private readonly ManualClock manual = new ManualClock();
        private FramedClock framed = null!;

        [SetUp]
        public void SetUp() => Schedule(() =>
        {
            Clear();
            Add(host = new Container { RelativeSizeAxes = Axes.Both });
        });

        /// <summary>A timeline still being simulated: no points, not complete, so it is PENDING at any
        /// time past zero.</summary>
        private static ReplayTimeline pending() => new ReplayTimeline();

        /// <summary>A finished timeline with one recorded state.</summary>
        private static ReplayTimeline ready(long score, int combo, double accuracy = 1)
        {
            var tl = new ReplayTimeline();
            tl.Record(new TimelinePoint(1000, score, combo, accuracy, false, "S"));
            tl.MarkComplete(2000);
            return tl;
        }

        private void build(params ReplayTimeline[] timelines)
        {
            var entrants = timelines
                           .Select((tl, i) => new KnockoutBoard.Entrant($"p{i}", Color4.White, tl))
                           .ToList();

            host.Child = board = new KnockoutBoard(entrants)
            {
                Rules = new KnockoutRules(),
                Height = 500,
            };

            host.Clock = framed = new FramedClock(manual);
            manual.CurrentTime = 0;
        }

        private void showAt(double time)
        {
            manual.CurrentTime = time;
            for (int i = 0; i < 60; i++)
            {
                framed.ProcessFrame();
                host.UpdateSubTree();
            }
        }

        private KnockoutBoard.Row rowFor(int index) => board.Rows.Single(r => r.PlayerIndex == index);

        [Test]
        public void AFreshLoadShowsNeutralZerosNotDashes()
        {
            AddStep("build three still-simulating players", () => build(pending(), pending(), pending()));
            AddUntilStep("rows built", () => board.Rows.Count == 3);
            AddStep("run the clock into the song while they simulate", () => showAt(5000));

            AddAssert("no row shows a dash placeholder", () => board.Rows.All(r =>
                !r.ScoreText.Contains('-') && !r.ComboText.Contains('-') && !r.AccuracyText.Contains('-')));

            AddAssert("every row reads neutral zeros", () => board.Rows.All(r =>
                r.ScoreText == "0" && r.ComboText == "0x" && r.AccuracyText == "0.00%"));
        }

        [Test]
        public void TheOrderHoldsStableWhileAnyPlayerIsStillSimulating()
        {
            // Player 2 is already recorded with a big score; players 0 and 1 are still simulating.
            AddStep("build a half-simulated field", () => build(pending(), pending(), ready(900_000, 500)));
            AddUntilStep("rows built", () => board.Rows.Count == 3);
            AddStep("run the clock forward", () => showAt(5000));

            // A live sort would rank player 2 straight to the top against two zero rows; holding the
            // order keeps them in drop order so the board does not churn (and rows do not collide).
            AddAssert("the order is held in drop order, not sorted", () =>
                board.DisplayOrder.SequenceEqual(new[] { 0, 1, 2 }));

            AddAssert("and the rows sit at distinct heights", () =>
            {
                var ys = board.Rows.Select(r => r.Y).OrderBy(y => y).ToList();
                return ys.Zip(ys.Skip(1), (a, b) => b - a).All(gap => gap > 1);
            });

            // Once every player is simulated, the board sorts normally and player 2 leads.
            AddStep("finish the other two", () => build(ready(100, 1), ready(200, 2), ready(900_000, 500)));
            AddUntilStep("rows built", () => board.Rows.Count == 3);
            AddStep("run forward", () => showAt(5000));
            AddAssert("now it sorts — the big score leads", () => board.DisplayOrder[0] == 2);
        }

        [Test]
        public void TheScoreIsAbbreviatedTheWayDanserDoesIt()
        {
            AddStep("build millions, thousands and a small score", () =>
                build(ready(16_850_000, 900), ready(15_300, 40), ready(900, 3)));
            AddUntilStep("rows built", () => board.Rows.Count == 3);
            AddStep("show the recorded moment", () => showAt(1500));

            AddAssert("millions read X.XXM", () => rowFor(0).ScoreText == "16.85M");
            AddAssert("thousands read X.XXK", () => rowFor(1).ScoreText == "15.30K");
            AddAssert("small scores read raw", () => rowFor(2).ScoreText == "900");
        }
    }
}
