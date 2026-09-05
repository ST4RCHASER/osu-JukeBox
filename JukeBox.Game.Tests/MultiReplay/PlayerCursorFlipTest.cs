#nullable enable

using System;
using JukeBox.Game.LazerPlayer;
using NUnit.Framework;
using osu.Game.Rulesets.Replays;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.Tests.MultiReplay
{
    /// <summary>
    /// The "Flip HR replay" mirror (item 6): an HR play's recorded cursor is in the vertically-flipped
    /// playfield, so to overlay a non-flipped shared chart its Y is mirrored (y → 384 − y). Pinned on
    /// the mirror itself — the combine decides WHEN to set FlipY (HR-ness differing from the driver,
    /// with the option on); this pins WHAT the flag does.
    /// </summary>
    [TestFixture]
    public class PlayerCursorFlipTest
    {
        private static PlayerCursor cursor(bool flip)
            => new PlayerCursor("p", Array.Empty<ReplayFrame>(), Color4.White) { FlipY = flip };

        [Test]
        public void FlipYMirrorsThePositionVertically()
        {
            var flipped = cursor(true);

            // Mirror about the playfield's vertical centre (384 / 2 = 192): 100 → 284, 192 → 192.
            Assert.That(flipped.OrientForTest(new Vector2(256, 100)), Is.EqualTo(new Vector2(256, 284)));
            Assert.That(flipped.OrientForTest(new Vector2(0, 192)), Is.EqualTo(new Vector2(0, 192)));
            Assert.That(flipped.OrientForTest(new Vector2(512, 0)), Is.EqualTo(new Vector2(512, 384)));
        }

        [Test]
        public void WithoutFlipThePositionIsUnchanged()
        {
            var plain = cursor(false);

            Assert.That(plain.OrientForTest(new Vector2(256, 100)), Is.EqualTo(new Vector2(256, 100)));
            Assert.That(plain.OrientForTest(new Vector2(512, 0)), Is.EqualTo(new Vector2(512, 0)));
        }
    }
}
