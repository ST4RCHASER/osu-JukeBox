using JukeBox.Game.Replays;
using JukeBox.Game.UI.Result;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Result
{
    /// <summary>
    /// The result screen's total score comes from the same recorded timeline the rail and grid read
    /// (stable ScoreV1 for a V1 play), and only falls back to the decoded lazer total when no play was
    /// recorded at all.
    /// </summary>
    [TestFixture]
    public class ResultScoreTest
    {
        [Test]
        public void TheRecordedTimelinesFinalScoreWinsOverTheDecodedTotal()
        {
            var timeline = new ReplayTimeline();
            timeline.Record(new TimelinePoint(1000, 12_000_000, 100, 0.99, false));
            timeline.Record(new TimelinePoint(2000, 36_600_000, 1481, 0.995, false));

            // 52.6M is what lazer's decoded ScoreInfo reads for the same play; 36.6M is what the rail shows.
            Assert.That(ResultScore.FinalScore(timeline, 52_600_000), Is.EqualTo(36_600_000));
        }

        [Test]
        public void WithNoRecordingTheDecodedTotalStandsIn()
        {
            Assert.That(ResultScore.FinalScore(null, 52_600_000), Is.EqualTo(52_600_000));
            Assert.That(ResultScore.FinalScore(new ReplayTimeline(), 52_600_000), Is.EqualTo(52_600_000));
        }
    }
}
