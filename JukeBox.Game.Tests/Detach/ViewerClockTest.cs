#nullable enable

using JukeBox.Game.Detach;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Detach
{
    /// <summary>
    /// The clock runs on a real stopwatch, so playing-state assertions use generous windows
    /// (position must be at-or-after the seek target but nowhere near a second ahead) rather
    /// than exact equality; paused-state assertions can be exact.
    /// </summary>
    [TestFixture]
    public class ViewerClockTest
    {
        private const double slack_ms = 500;

        [Test]
        public void FirstPlayingSnapshotStartsAtPosition()
        {
            var clock = new ViewerClock();

            clock.Apply(5000, 1, true);

            Assert.That(clock.IsRunning, Is.True);
            Assert.That(clock.CurrentTime, Is.InRange(5000, 5000 + slack_ms));
            Assert.That(clock.SnapCount, Is.Zero, "a run-state change is an authoritative seek, not a drift snap");
        }

        [Test]
        public void PausedSnapshotStopsExactlyAtPosition()
        {
            var clock = new ViewerClock();

            clock.Apply(3000, 1, false);

            Assert.That(clock.IsRunning, Is.False);
            Assert.That(clock.CurrentTime, Is.EqualTo(3000));
            Assert.That(clock.SnapCount, Is.Zero);
        }

        [Test]
        public void PauseWhilePlayingFreezesAtReportedPosition()
        {
            var clock = new ViewerClock();

            clock.Apply(1000, 1, true);
            clock.Apply(1005, 1, false);

            Assert.That(clock.IsRunning, Is.False);
            Assert.That(clock.CurrentTime, Is.EqualTo(1005));
            Assert.That(clock.SnapCount, Is.Zero);
        }

        [Test]
        public void SmallDriftIsLeftToTheLocalClock()
        {
            var clock = new ViewerClock();

            clock.Apply(1000, 1, true);
            // A correction within the snap threshold of where the local clock already is.
            clock.Apply(1000 + ViewerClock.SnapThresholdMs / 2, 1, true);

            Assert.That(clock.SnapCount, Is.Zero);
            Assert.That(clock.LastDeltaMs, Is.InRange(-ViewerClock.SnapThresholdMs, ViewerClock.SnapThresholdMs));
        }

        [Test]
        public void DriftBeyondThresholdSnaps()
        {
            var clock = new ViewerClock();

            clock.Apply(1000, 1, true);
            // The main app seeked far ahead: local ≈1000 vs reported 30000.
            clock.Apply(30000, 1, true);

            Assert.That(clock.SnapCount, Is.EqualTo(1));
            Assert.That(clock.CurrentTime, Is.InRange(30000, 30000 + slack_ms));
        }

        [Test]
        public void RateIsForwardedToTheClock()
        {
            var clock = new ViewerClock();

            clock.Apply(0, 1.5, true);

            Assert.That(clock.FramedClock.Rate, Is.EqualTo(1.5));
        }
    }
}
