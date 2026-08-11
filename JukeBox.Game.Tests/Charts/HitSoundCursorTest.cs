#nullable enable

using JukeBox.Game.Charts;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Charts
{
    [TestFixture]
    public class HitSoundCursorTest
    {
        private static HitSoundCursor makeCursor() => new HitSoundCursor(new double[] { 100, 200, 300 });

        [Test]
        public void EventsFireOnceAsTimeAdvances()
        {
            var cursor = makeCursor();

            Assert.That(cursor.Advance(50), Is.Empty);
            Assert.That(cursor.Advance(150), Is.EqualTo(new[] { 0 }));
            Assert.That(cursor.Advance(150), Is.Empty, "already-fired event must not re-fire");
            Assert.That(cursor.Advance(250), Is.EqualTo(new[] { 1 }));
            Assert.That(cursor.Advance(301), Is.EqualTo(new[] { 2 }));
            Assert.That(cursor.Advance(10000), Is.Empty);
        }

        [Test]
        public void MultipleEventsDueInOneFrameAllFire()
        {
            var cursor = makeCursor();

            Assert.That(cursor.Advance(90), Is.Empty);
            // 100 and 200 both became due within the catch-up window.
            Assert.That(cursor.Advance(220), Is.EqualTo(new[] { 0, 1 }));
        }

        [Test]
        public void BackwardSeekRefiresReplayedEvents()
        {
            var cursor = makeCursor();

            Assert.That(cursor.Advance(250), Is.EqualTo(new[] { 0, 1 }));

            // Rewind before the first event: replaying the section fires them again.
            Assert.That(cursor.Advance(50), Is.Empty);
            Assert.That(cursor.Advance(150), Is.EqualTo(new[] { 0 }));
            Assert.That(cursor.Advance(250), Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void LargeForwardSeekSuppressesCatchUpBurst()
        {
            var cursor = makeCursor();

            Assert.That(cursor.Advance(0), Is.Empty);

            // Jumping far past all events must not machine-gun the stale ones...
            Assert.That(cursor.Advance(5000), Is.Empty);

            // ...and the cursor is fully advanced afterwards (no delayed firing either).
            Assert.That(cursor.Advance(5016), Is.Empty);
        }

        [Test]
        public void SmallBackwardJitterDoesNotRefire()
        {
            var cursor = makeCursor();

            Assert.That(cursor.Advance(150), Is.EqualTo(new[] { 0 }));

            // A sub-tolerance backward wobble (clock jitter) must be a no-op...
            Assert.That(cursor.Advance(120), Is.Empty);

            // ...including not re-firing the event when time moves forward past it again.
            Assert.That(cursor.Advance(150), Is.Empty);
            Assert.That(cursor.Advance(250), Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void BackwardJumpBeyondToleranceIsARealSeek()
        {
            var cursor = makeCursor();

            Assert.That(cursor.Advance(150), Is.EqualTo(new[] { 0 }));

            // 150 → 90 is a 60ms jump — beyond tolerance, so the cursor rewinds and the event
            // re-fires on the replay.
            Assert.That(cursor.Advance(90), Is.Empty);
            Assert.That(cursor.Advance(150), Is.EqualTo(new[] { 0 }));
        }

        [Test]
        public void SeekBackToExactEventTimeIncludesIt()
        {
            var cursor = makeCursor();

            cursor.Advance(400);
            Assert.That(cursor.Advance(200), Is.EqualTo(new[] { 1 }));
        }
    }
}
