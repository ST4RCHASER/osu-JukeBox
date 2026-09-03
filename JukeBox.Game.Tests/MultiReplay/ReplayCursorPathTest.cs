#nullable enable

using System.Collections.Generic;
using JukeBox.Game.Replays;
using NUnit.Framework;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Replays;
using osuTK;

namespace JukeBox.Game.Tests.MultiReplay
{
    [TestFixture]
    public class ReplayCursorPathTest
    {
        private static List<ReplayFrame> frames(params (double time, float x, float y)[] points)
        {
            var list = new List<ReplayFrame>();

            foreach (var (time, x, y) in points)
                list.Add(new OsuReplayFrame(time, new Vector2(x, y)));

            return list;
        }

        [Test]
        public void ItReadsAFramesPositionExactly()
        {
            var path = frames((0, 0, 0), (100, 100, 50));

            Assert.That(ReplayCursorPath.PositionAt(path, 0), Is.EqualTo(new Vector2(0, 0)));
            Assert.That(ReplayCursorPath.PositionAt(path, 100), Is.EqualTo(new Vector2(100, 50)));
        }

        /// <summary>
        /// Between frames the cursor has to MOVE. Replay frames are tens of milliseconds apart, so
        /// snapping to the nearest one would make every cursor visibly stutter.
        /// </summary>
        [Test]
        public void ItInterpolatesBetweenFrames()
        {
            var path = frames((0, 0, 0), (100, 100, 50));
            var middle = ReplayCursorPath.PositionAt(path, 50);

            Assert.That(middle!.Value.X, Is.EqualTo(50).Within(0.001));
            Assert.That(middle.Value.Y, Is.EqualTo(25).Within(0.001));
        }

        /// <summary>
        /// Outside the replay the nearest frame is held. Extrapolating would draw a cursor sailing
        /// off through the intro, in a direction the player never moved.
        /// </summary>
        [Test]
        public void OutsideTheReplayTheNearestFrameIsHeld()
        {
            var path = frames((100, 10, 10), (200, 20, 20));

            Assert.That(ReplayCursorPath.PositionAt(path, -5000), Is.EqualTo(new Vector2(10, 10)));
            Assert.That(ReplayCursorPath.PositionAt(path, 999999), Is.EqualTo(new Vector2(20, 20)));
        }

        [Test]
        public void AnEmptyReplayHasNoPosition()
        {
            Assert.That(ReplayCursorPath.PositionAt(new List<ReplayFrame>(), 0), Is.Null);
        }

        /// <summary>Two frames at the same instant must not divide by zero — real replays have them.</summary>
        [Test]
        public void DuplicateTimestampsAreSurvivable()
        {
            var path = frames((100, 10, 10), (100, 99, 99), (200, 20, 20));

            Assert.That(ReplayCursorPath.PositionAt(path, 100), Is.Not.Null);
        }

        [Test]
        public void EveryFrameIsReachableIncludingTheEnds()
        {
            var path = frames((0, 1, 1), (50, 2, 2), (100, 3, 3), (150, 4, 4));

            Assert.That(ReplayCursorPath.PositionAt(path, 0)!.Value.X, Is.EqualTo(1).Within(0.001));
            Assert.That(ReplayCursorPath.PositionAt(path, 150)!.Value.X, Is.EqualTo(4).Within(0.001));
        }
    }
}
