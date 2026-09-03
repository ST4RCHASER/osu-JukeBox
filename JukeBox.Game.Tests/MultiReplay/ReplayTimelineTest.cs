#nullable enable

using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Replays;
using NUnit.Framework;

namespace JukeBox.Game.Tests.MultiReplay
{
    /// <summary>
    /// The recorded play. Its whole reason for existing is that a lookup by time gives the same
    /// answer however the viewer got to that time, so that is what these check.
    /// </summary>
    [TestFixture]
    public class ReplayTimelineTest
    {
        private static ReplayTimeline threeHits()
        {
            var timeline = new ReplayTimeline();
            timeline.Record(new TimelinePoint(1000, 300, 1, 1.00, false));
            timeline.Record(new TimelinePoint(2000, 600, 2, 1.00, false));
            timeline.Record(new TimelinePoint(3000, 900, 3, 1.00, false));
            return timeline;
        }

        [Test]
        public void BeforeThePlayStartsItReadsAnUnplayedScore()
        {
            var point = threeHits().At(0);

            Assert.That(point.Score, Is.Zero);
            Assert.That(point.Combo, Is.Zero);

            // 100%, not 0% — an unjudged play has not got anything wrong yet, which is what lazer
            // itself reads and what the scoreboard has to match.
            Assert.That(point.Accuracy, Is.EqualTo(1));
        }

        [Test]
        public void ALookupReturnsTheStateAsOfThatMoment()
        {
            var timeline = threeHits();

            Assert.That(timeline.At(1500).Combo, Is.EqualTo(1), "between judgements, the last one stands");
            Assert.That(timeline.At(2000).Combo, Is.EqualTo(2), "exactly on a judgement, it counts");
            Assert.That(timeline.At(99999).Combo, Is.EqualTo(3), "past the end, the play holds its final state");
        }

        /// <summary>
        /// The property the whole design exists for: the answer cannot depend on the route taken to
        /// the question. A running total fails this the moment a seek skips a judgement.
        /// </summary>
        [Test]
        public void TheAnswerIsTheSameWhicheverWayTheViewerGotThere()
        {
            var timeline = threeHits();

            var walkedForwards = timeline.At(2500);

            // Arrived at by jumping to the end and coming back, which is what a scrub is.
            timeline.At(99999);
            var arrivedBackwards = timeline.At(2500);

            Assert.That(arrivedBackwards, Is.EqualTo(walkedForwards));
        }

        [Test]
        public void EveryPointIsReachableIncludingTheFirstAndLast()
        {
            var timeline = threeHits();

            // A binary search with the midpoint rounded the wrong way hangs or skips an end; walking
            // every boundary is how that gets caught rather than assumed.
            Assert.That(timeline.At(1000).Score, Is.EqualTo(300));
            Assert.That(timeline.At(999).Score, Is.Zero);
            Assert.That(timeline.At(3000).Score, Is.EqualTo(900));
            Assert.That(timeline.At(2999).Score, Is.EqualTo(600));
        }

        [Test]
        public void AJudgementArrivingOutOfOrderIsRefusedRatherThanCorruptingTheSearch()
        {
            var timeline = threeHits();
            timeline.Record(new TimelinePoint(1500, 99999, 99, 0.1, true));

            Assert.That(timeline.Points, Has.Count.EqualTo(3), "the late arrival is dropped");
            Assert.That(timeline.At(1500).Combo, Is.EqualTo(1), "and the search still reads correctly");
        }

        [Test]
        public void SimulatedToTracksHowFarThePlayHasBeenRecorded()
        {
            var timeline = new ReplayTimeline();
            Assert.That(timeline.SimulatedTo, Is.Zero);
            Assert.That(timeline.Complete, Is.False);

            timeline.Record(new TimelinePoint(4200, 100, 1, 1, false));
            Assert.That(timeline.SimulatedTo, Is.EqualTo(4200));
            Assert.That(timeline.Complete, Is.False, "recording a judgement is not finishing the map");

            timeline.MarkComplete(60000);
            Assert.That(timeline.Complete, Is.True);
            Assert.That(timeline.SimulatedTo, Is.EqualTo(60000), "which runs to the END of the map, not the last hit");
        }

        /// <summary>
        /// Asking about a moment the simulation has not reached is a question with no answer yet,
        /// and the board has to say so. The timeline itself will happily return the last thing it
        /// recorded — which reads as a real score for a moment the player has not got to.
        /// </summary>
        [Test]
        public void AMomentBeyondTheSimulationIsPendingRatherThanAnswered()
        {
            var timeline = threeHits();

            Assert.That(KnockoutBoard.IsPending(timeline, 2000), Is.False, "inside what has been recorded");
            Assert.That(KnockoutBoard.IsPending(timeline, 90000), Is.True, "past it, with more still coming");

            // Once the play is fully recorded, past the end is not pending — it is the final score,
            // which is a real answer.
            timeline.MarkComplete(60000);
            Assert.That(KnockoutBoard.IsPending(timeline, 90000), Is.False);
        }

        [Test]
        public void AComboBreakIsFoundAtItsOwnTime()
        {
            var timeline = new ReplayTimeline();
            timeline.Record(new TimelinePoint(1000, 300, 1, 1.0, false));
            timeline.Record(new TimelinePoint(2000, 300, 0, 0.5, true));

            Assert.That(timeline.FirstComboBreak(0), Is.EqualTo(2000));
            Assert.That(timeline.FirstComboBreak(5), Is.Null, "and is forgiven inside the grace period");
        }
    }
}
