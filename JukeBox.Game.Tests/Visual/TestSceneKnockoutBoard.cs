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

            hostBoard(entrants);
        }

        private void buildWithFrames(ReplayTimeline timeline, System.Collections.Generic.IReadOnlyList<osu.Game.Rulesets.Replays.ReplayFrame> frames)
            => hostBoard(new System.Collections.Generic.List<KnockoutBoard.Entrant>
            {
                new KnockoutBoard.Entrant("p0", Color4.White, timeline, string.Empty, frames),
            });

        private void hostBoard(System.Collections.Generic.List<KnockoutBoard.Entrant> entrants)
        {
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
        public void TheKeyBarsLightForWhicheverButtonIsHeld()
        {
            AddStep("build a player who holds the left button from 1000 to 2000", () =>
            {
                var frames = new System.Collections.Generic.List<osu.Game.Rulesets.Replays.ReplayFrame>
                {
                    new osu.Game.Rulesets.Osu.Replays.OsuReplayFrame(0, osuTK.Vector2.Zero),
                    new osu.Game.Rulesets.Osu.Replays.OsuReplayFrame(1000, osuTK.Vector2.Zero, osu.Game.Rulesets.Osu.OsuAction.LeftButton),
                    new osu.Game.Rulesets.Osu.Replays.OsuReplayFrame(2000, osuTK.Vector2.Zero),
                };
                buildWithFrames(ready(100, 1), frames);
            });
            AddUntilStep("row built", () => board.Rows.Count == 1);

            AddStep("show before the press", () => showAt(500));
            AddAssert("neither key is lit", () => !rowFor(0).LeftKeyHeld && !rowFor(0).RightKeyHeld);

            AddStep("show during the press", () => showAt(1500));
            AddAssert("the left key is lit, the right is not", () => rowFor(0).LeftKeyHeld && !rowFor(0).RightKeyHeld);

            AddStep("show after the release", () => showAt(2500));
            AddAssert("neither key is lit again", () => !rowFor(0).LeftKeyHeld && !rowFor(0).RightKeyHeld);
        }

        /// <summary>
        /// A long name has to stay in its own column. The name used to auto-size and run rightward
        /// under the right-pinned combo, so on a real board the combo number was drawn on top of the
        /// player's name. The name column is now reserved at the field's longest name+mods and the
        /// board widened to fit, so the combo begins after the name ends.
        /// </summary>
        [Test]
        public void ALongNameDoesNotCollideWithTheComboColumn()
        {
            AddStep("build a field whose longest entry is a long name plus mods", () =>
                hostBoard(new System.Collections.Generic.List<KnockoutBoard.Entrant>
                {
                    new KnockoutBoard.Entrant("ALongPlayerNameHere", Color4.White, ready(1_000_000, 500), "+HDDTHR"),
                    new KnockoutBoard.Entrant("x", Color4.White, ready(500_000, 200)),
                }));
            AddUntilStep("rows built", () => board.Rows.Count == 2);
            AddStep("show a recorded moment", () => showAt(1500));

            // Every row, not just the long one: the column is reserved field-wide, so the short name's
            // combo lines up with the long one's and neither is overdrawn.
            AddAssert("the name column ends at or before the combo column begins", () =>
                board.Rows.All(r => r.NameRightEdge <= r.ComboLeftEdge + 0.5f));
        }

        /// <summary>
        /// The scoring-version tag is drawn beside the mods as its own run, and the name column still
        /// leaves room for it — the width budget counts the tag, so it does not push into the combo
        /// column any more than the mods do.
        /// </summary>
        [Test]
        public void TheScoringVersionTagIsShownAndFits()
        {
            AddStep("build entrants with version tags", () =>
                hostBoard(new System.Collections.Generic.List<KnockoutBoard.Entrant>
                {
                    new KnockoutBoard.Entrant("WhiteCat", Color4.White, ready(1_000_000, 500), "+HDDT", null, "V1"),
                    new KnockoutBoard.Entrant("someone", Color4.White, ready(500_000, 200), string.Empty, null, "Classic"),
                }));
            AddUntilStep("rows built", () => board.Rows.Count == 2);
            AddStep("show a recorded moment", () => showAt(1500));

            AddAssert("each row shows its version tag", () =>
                rowFor(0).VersionText == "V1" && rowFor(1).VersionText == "Classic");
            AddAssert("the name column still ends before the combo column", () =>
                board.Rows.All(r => r.NameRightEdge <= r.ComboLeftEdge + 0.5f));
        }

        /// <summary>
        /// The hit badge pops in large on a fresh drop, then settles to its normal size and fades to
        /// nothing over about 1.5s. The old hard on/off flashed by too fast to read; this is driven
        /// from the timeline by elapsed time so it is the same after a seek.
        /// </summary>
        [Test]
        public void TheHitBadgePopsInThenSettlesAndFades()
        {
            AddStep("build a player who missed at 1000ms", () =>
            {
                var tl = new ReplayTimeline();
                tl.Record(new TimelinePoint(1000, 100, 0, 0.9, true, "A", 0, 5,
                    osu.Game.Rulesets.Scoring.HitResult.Miss));
                tl.MarkComplete(6000);
                build(tl);
            });
            AddUntilStep("row built", () => board.Rows.Count == 1);

            AddStep("just after the miss", () => showAt(1050));
            AddAssert("the badge shows, popped large and fully opaque", () =>
                rowFor(0).JudgementText == "X" && rowFor(0).JudgementScale > 1.3f && rowFor(0).JudgementAlpha > 0.9f);

            AddStep("part way through its life", () => showAt(1600));
            AddAssert("it has settled to about normal size, still visible", () =>
                rowFor(0).JudgementScale < 1.1f && rowFor(0).JudgementAlpha > 0.5f);

            AddStep("near the end of its life", () => showAt(2400));
            AddAssert("it has faded to almost nothing", () =>
                rowFor(0).JudgementText == "X" && rowFor(0).JudgementAlpha < 0.2f);

            AddStep("after its life", () => showAt(2700));
            AddAssert("it is gone", () =>
                rowFor(0).JudgementText.Length == 0 && rowFor(0).JudgementAlpha == 0);
        }

        /// <summary>
        /// Remove-row-after-knockout: with the option on, a knocked-out player's WHOLE row is removed
        /// (faded to nothing); with it off, an eliminated row only dims and stays. Asserted on the row's
        /// visibility, so ignoring the option fails.
        /// </summary>
        [Test]
        public void RemoveRowAfterKnockoutRemovesAnEliminatedPlayersRow()
        {
            void buildBreakingField(bool removeRow) => host.Child = board = new KnockoutBoard(
                new System.Collections.Generic.List<KnockoutBoard.Entrant>
                {
                    new KnockoutBoard.Entrant("breaker", Color4.White, brokeAt(2000)),
                    new KnockoutBoard.Entrant("survivor", Color4.White, ready(2000, 10)),
                })
            {
                Rules = new KnockoutRules(KnockoutMode.ComboBreak, GraceEndSeconds: 0),
                RemoveRowOnKnockout = removeRow,
                Height = 500,
            };

            // The removal is a real transform, so the clock has to actually advance for it to run —
            // showAt freezes time at one instant, which never lets a fade finish.
            void runClockTo(double target)
            {
                for (double t = manual.CurrentTime + 20; t <= target; t += 20)
                {
                    manual.CurrentTime = t;
                    framed.ProcessFrame();
                    host.UpdateSubTree();
                }
            }

            AddStep("build a breaking field, row-removal ON", () =>
            {
                buildBreakingField(removeRow: true);
                host.Clock = framed = new FramedClock(manual);
                manual.CurrentTime = 0;
            });
            AddUntilStep("rows built", () => board.Rows.Count == 2);

            AddStep("play up to just before the break", () => runClockTo(1800));
            AddAssert("both rows are shown", () => board.Rows.All(r => r.RowShown));

            AddStep("play past the break and let the removal finish", () => runClockTo(3300));
            AddAssert("the eliminated player's row is removed", () => !rowFor(0).RowShown);
            AddAssert("the survivor's row stays", () => rowFor(1).RowShown);

            AddStep("rebuild with row-removal OFF", () =>
            {
                buildBreakingField(removeRow: false);
                manual.CurrentTime = 0;
            });
            AddUntilStep("rows built", () => board.Rows.Count == 2);
            AddStep("play past the break", () => runClockTo(3300));
            AddAssert("the eliminated player's row stays (only dimmed) when the option is off", () => rowFor(0).RowShown);
        }

        /// <summary>A timeline that holds a combo then breaks it at <paramref name="breakTime"/>.</summary>
        private static ReplayTimeline brokeAt(double breakTime)
        {
            var tl = new ReplayTimeline();
            tl.Record(new TimelinePoint(1000, 1000, 5, 1.0, false, "A"));
            tl.Record(new TimelinePoint(breakTime, 1000, 0, 0.9, true, "B", 0, 5));
            tl.MarkComplete(breakTime + 1000);
            return tl;
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
