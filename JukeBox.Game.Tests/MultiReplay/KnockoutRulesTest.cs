#nullable enable

using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Replays;
using NUnit.Framework;

namespace JukeBox.Game.Tests.MultiReplay
{
    /// <summary>
    /// The rules of a knockout, tested as arithmetic on recorded plays. None of this needs a game
    /// host, which is the whole reason it was built as a value type: "does this player survive to
    /// forty seconds" should be answerable without watching a replay to find out.
    /// </summary>
    [TestFixture]
    public class KnockoutRulesTest
    {
        /// <summary>
        /// A play as a list of (time, combo) pairs. Combo dropping to zero is a break, which is what
        /// the rules key off; score and accuracy are derived so the fixture stays readable.
        /// </summary>
        private static ReplayTimeline play(params (double time, int combo)[] beats)
        {
            var timeline = new ReplayTimeline();
            int hits = 0;
            int misses = 0;

            foreach (var (time, combo) in beats)
            {
                bool broke = combo == 0;

                if (broke)
                    misses++;
                else
                    hits++;

                timeline.Record(new TimelinePoint(time, hits * 1000L, combo,
                    (double)hits / (hits + misses), broke));
            }

            return timeline;
        }

        /// <summary>
        /// The defaults in the declaration are the defaults you actually get.
        ///
        /// <para>
        /// This exists because for a while they were not. KnockoutRules began as a record STRUCT,
        /// and a struct's parameterless constructor zero-fills rather than running the primary
        /// constructor — so <c>new KnockoutRules()</c> produced GraceEndSeconds 0 and LiveSort
        /// FALSE while the signature said 10 and true. Nothing errored; the board simply never
        /// re-ordered, and the only symptom was a scoreboard that looked plausible and was wrong.
        /// </para>
        /// </summary>
        [Test]
        public void TheDeclaredDefaultsAreTheOnesYouGet()
        {
            var rules = new KnockoutRules();

            Assert.That(rules.Mode, Is.EqualTo(KnockoutMode.Showcase));
            Assert.That(rules.GraceEndSeconds, Is.EqualTo(10));
            Assert.That(rules.LiveSort, Is.True);
            Assert.That(rules.SortBy, Is.EqualTo(KnockoutSort.Score));
        }

        [Test]
        public void ShowcaseNeverKnocksAnybodyOut()
        {
            var rules = new KnockoutRules();

            Assert.That(rules.Mode, Is.EqualTo(KnockoutMode.Showcase), "elimination must be opt-in");

            // A play that breaks combo repeatedly and still survives, because the mode says so.
            var messy = play((1000, 1), (2000, 0), (3000, 1), (4000, 0));

            Assert.That(rules.KnockedOutAt(messy), Is.Null);
            Assert.That(rules.AliveAt(messy, 99999), Is.True);
        }

        [Test]
        public void ComboBreakKnocksOutAtTheFirstBreak()
        {
            var rules = new KnockoutRules(KnockoutMode.ComboBreak, GraceEndSeconds: 0);
            var timeline = play((1000, 1), (2000, 2), (3000, 0), (4000, 1), (5000, 0));

            Assert.That(rules.KnockedOutAt(timeline), Is.EqualTo(3000), "the FIRST break ends it, not the last");
            Assert.That(rules.AliveAt(timeline, 2999), Is.True);
            Assert.That(rules.AliveAt(timeline, 3000), Is.False, "out at the moment of the break, not after it");
            Assert.That(rules.AliveAt(timeline, 4000), Is.False);
        }

        /// <summary>
        /// The grace period is the difference between a knockout worth watching and one that is over
        /// before the viewer knows who is playing.
        /// </summary>
        [Test]
        public void BreaksInsideTheGracePeriodAreForgiven()
        {
            var timeline = play((1000, 1), (2000, 0), (30000, 1), (40000, 0));

            var forgiving = new KnockoutRules(KnockoutMode.ComboBreak, GraceEndSeconds: 10);
            Assert.That(forgiving.KnockedOutAt(timeline), Is.EqualTo(40000),
                "the break at 2s is inside the ten-second grace and must not count");

            var unforgiving = new KnockoutRules(KnockoutMode.ComboBreak, GraceEndSeconds: 0);
            Assert.That(unforgiving.KnockedOutAt(timeline), Is.EqualTo(2000));
        }

        [Test]
        public void APerfectPlayIsNeverKnockedOut()
        {
            var rules = new KnockoutRules(KnockoutMode.ComboBreak, GraceEndSeconds: 0);
            var clean = play((1000, 1), (2000, 2), (3000, 3));

            Assert.That(rules.KnockedOutAt(clean), Is.Null);
            Assert.That(rules.AliveAt(clean, 99999), Is.True);
        }

        [Test]
        public void ImperfectionModeEndsOnTheFirstDroppedJudgement()
        {
            var rules = new KnockoutRules(KnockoutMode.Imperfection);

            // Combo intact throughout — only accuracy slips — so this is a case the combo rule
            // would let live, which is what makes the two modes different rather than aliases.
            var timeline = new ReplayTimeline();
            timeline.Record(new TimelinePoint(1000, 1000, 1, 1.0, false));
            timeline.Record(new TimelinePoint(2000, 1800, 2, 0.95, false));

            Assert.That(rules.KnockedOutAt(timeline), Is.EqualTo(2000));
            Assert.That(new KnockoutRules(KnockoutMode.ComboBreak, 0).KnockedOutAt(timeline), Is.Null);
        }

        [Test]
        public void TheBoardIsOrderedByScoreAsThePlaysDiverge()
        {
            var rules = new KnockoutRules(KnockoutMode.Showcase);

            // Player 1 leads early; player 0 overtakes by the end. A board that reads the FINAL
            // scores would show the same order at both times, which is the bug this guards.
            var slowStarter = new ReplayTimeline();
            slowStarter.Record(new TimelinePoint(1000, 100, 1, 1, false));
            slowStarter.Record(new TimelinePoint(5000, 900, 5, 1, false));

            var fastStarter = new ReplayTimeline();
            fastStarter.Record(new TimelinePoint(1000, 500, 1, 1, false));
            fastStarter.Record(new TimelinePoint(5000, 600, 5, 1, false));

            var timelines = new List<ReplayTimeline> { slowStarter, fastStarter };

            Assert.That(rules.Standings(timelines, 1000), Is.EqualTo(new[] { 1, 0 }), "early, the fast starter leads");
            Assert.That(rules.Standings(timelines, 5000), Is.EqualTo(new[] { 0, 1 }), "and is overtaken");
        }

        [Test]
        public void TurningLiveSortOffHoldsTheOrder()
        {
            var slowStarter = new ReplayTimeline();
            slowStarter.Record(new TimelinePoint(1000, 100, 1, 1, false));
            slowStarter.Record(new TimelinePoint(5000, 900, 5, 1, false));

            var fastStarter = new ReplayTimeline();
            fastStarter.Record(new TimelinePoint(1000, 500, 1, 1, false));
            fastStarter.Record(new TimelinePoint(5000, 600, 5, 1, false));

            var timelines = new List<ReplayTimeline> { slowStarter, fastStarter };
            var held = new KnockoutRules(KnockoutMode.Showcase, LiveSort: false);

            Assert.That(held.Standings(timelines, 1000), Is.EqualTo(held.Standings(timelines, 5000)),
                "with live sort off the board must not re-order as the plays diverge");
        }

        [Test]
        public void SortingByAccuracyIsNotSortingByScore()
        {
            // The higher score has the worse accuracy, so the two orderings must disagree — a test
            // where they agree cannot tell the setting is being read.
            var grinder = new ReplayTimeline();
            grinder.Record(new TimelinePoint(1000, 5000, 10, 0.90, false));

            var precise = new ReplayTimeline();
            precise.Record(new TimelinePoint(1000, 3000, 5, 0.99, false));

            var timelines = new List<ReplayTimeline> { grinder, precise };

            Assert.That(new KnockoutRules(SortBy: KnockoutSort.Score).Standings(timelines, 1000), Is.EqualTo(new[] { 0, 1 }));
            Assert.That(new KnockoutRules(SortBy: KnockoutSort.Accuracy).Standings(timelines, 1000), Is.EqualTo(new[] { 1, 0 }));
        }

        /// <summary>
        /// The eliminated stay on the board, below everyone still playing — a knockout the viewer
        /// cannot see happen is just a name going missing.
        /// </summary>
        [Test]
        public void TheKnockedOutSinkBelowTheSurvivorsButRemainOnTheBoard()
        {
            var rules = new KnockoutRules(KnockoutMode.ComboBreak, GraceEndSeconds: 0);

            var out_ = play((1000, 0));                        // out immediately, huge score
            out_.Record(new TimelinePoint(1100, 999999, 0, 0.5, false));

            var alive = play((1000, 1));                       // still in it, tiny score

            var timelines = new List<ReplayTimeline> { out_, alive };
            var standings = rules.Standings(timelines, 5000);

            Assert.That(standings, Is.EqualTo(new[] { 1, 0 }), "alive outranks out, whatever the score says");
            Assert.That(standings, Has.Count.EqualTo(2), "and the eliminated player is still listed");
        }

        [Test]
        public void AmongTheEliminatedTheOneWhoLastedLongerRanksHigher()
        {
            var rules = new KnockoutRules(KnockoutMode.ComboBreak, GraceEndSeconds: 0);

            var earlyExit = play((2000, 0));
            var lateExit = play((1000, 1), (8000, 0));

            var standings = rules.Standings(new List<ReplayTimeline> { earlyExit, lateExit }, 20000);

            Assert.That(standings, Is.EqualTo(new[] { 1, 0 }));
        }

        /// <summary>
        /// A knockout has to END with somebody. Eliminating the whole field leaves an empty board,
        /// which is a worse finish than one survivor; the reference has the same floor.
        /// </summary>
        [Test]
        public void EliminationStopsOnceTheFieldIsDownToTheFloor()
        {
            var rules = new KnockoutRules(KnockoutMode.ComboBreak, GraceEndSeconds: 0, MinimumAlive: 1);

            // Everybody breaks, at different times. With no floor there would be nobody left at all.
            var timelines = new List<ReplayTimeline>
            {
                play((1000, 1), (3000, 0)),
                play((1000, 1), (5000, 0)),
                play((1000, 1), (7000, 0)),
            };

            Assert.That(rules.AliveCount(timelines, 20000), Is.EqualTo(1), "the last one standing is spared");

            // And it is whoever lasted LONGEST who survives, not an arbitrary one.
            Assert.That(rules.AliveAt(timelines, 2, 20000), Is.True);
            Assert.That(rules.AliveAt(timelines, 0, 20000), Is.False);
            Assert.That(rules.AliveAt(timelines, 1, 20000), Is.False);
        }

        [Test]
        public void AHigherFloorSparesMore()
        {
            var timelines = new List<ReplayTimeline>
            {
                play((1000, 1), (3000, 0)),
                play((1000, 1), (5000, 0)),
                play((1000, 1), (7000, 0)),
                play((1000, 1), (9000, 0)),
            };

            Assert.That(new KnockoutRules(KnockoutMode.ComboBreak, 0, MinimumAlive: 1).AliveCount(timelines, 20000), Is.EqualTo(1));
            Assert.That(new KnockoutRules(KnockoutMode.ComboBreak, 0, MinimumAlive: 2).AliveCount(timelines, 20000), Is.EqualTo(2));

            // A floor larger than the field cannot eliminate anybody.
            Assert.That(new KnockoutRules(KnockoutMode.ComboBreak, 0, MinimumAlive: 99).AliveCount(timelines, 20000), Is.EqualTo(4));
        }

        /// <summary>
        /// The playfield cue fires only for breaks big enough to point at. Announcing every dropped
        /// combo across a large field is a continuous flicker, and a cue that never stops carries
        /// nothing.
        /// </summary>
        [Test]
        public void OnlySubstantialBreaksAreWorthAnnouncing()
        {
            var rules = new KnockoutRules(BubbleMinimumCombo: 200);

            Assert.That(rules.WorthAnnouncing(500), Is.True);
            Assert.That(rules.WorthAnnouncing(200), Is.True, "exactly at the threshold counts");
            Assert.That(rules.WorthAnnouncing(199), Is.False);
            Assert.That(rules.WorthAnnouncing(3), Is.False, "a three-combo fumble is not news");
        }

        [Test]
        public void TheAnnounceThresholdIsAdjustable()
        {
            Assert.That(new KnockoutRules(BubbleMinimumCombo: 0).WorthAnnouncing(1), Is.True,
                "zero announces everything");
            Assert.That(new KnockoutRules(BubbleMinimumCombo: 1000).WorthAnnouncing(999), Is.False);
        }

        [Test]
        public void SortingByPerformanceIsNotSortingByScore()
        {
            // The bigger score carries the smaller pp, so the two orderings must disagree — a
            // fixture where they agree cannot tell the setting is being read.
            var grinder = new ReplayTimeline();
            grinder.Record(new TimelinePoint(1000, 900_000, 10, 0.95, false, "A", 120));

            var precise = new ReplayTimeline();
            precise.Record(new TimelinePoint(1000, 500_000, 5, 0.99, false, "S", 340));

            var timelines = new List<ReplayTimeline> { grinder, precise };

            Assert.That(new KnockoutRules(SortBy: KnockoutSort.Score).Standings(timelines, 1000), Is.EqualTo(new[] { 0, 1 }));
            Assert.That(new KnockoutRules(SortBy: KnockoutSort.Performance).Standings(timelines, 1000), Is.EqualTo(new[] { 1, 0 }));
        }

        [Test]
        public void CursorsGrowAsTheFieldThins()
        {
            Assert.That(KnockoutRules.CursorScale(4, 4), Is.EqualTo(1).Within(0.001), "everyone in: smallest");
            Assert.That(KnockoutRules.CursorScale(1, 4), Is.EqualTo(2.2).Within(0.001), "one left: biggest");
            Assert.That(KnockoutRules.CursorScale(2, 4), Is.GreaterThan(KnockoutRules.CursorScale(3, 4)),
                "and monotonically between");

            // A lone player is the only one there is, so they get the full size rather than a
            // division by zero.
            Assert.That(KnockoutRules.CursorScale(1, 1), Is.EqualTo(2.2).Within(0.001));
        }

        [Test]
        public void AliveCountFallsAsPlayersGoOut()
        {
            var rules = new KnockoutRules(KnockoutMode.ComboBreak, GraceEndSeconds: 0);

            var timelines = new List<ReplayTimeline>
            {
                play((2000, 0)),
                play((1000, 1), (5000, 0)),
                play((1000, 1), (9000, 2)),
            };

            Assert.That(rules.AliveCount(timelines, 0), Is.EqualTo(3));
            Assert.That(rules.AliveCount(timelines, 3000), Is.EqualTo(2));
            Assert.That(rules.AliveCount(timelines, 6000), Is.EqualTo(1));
        }
    }
}
