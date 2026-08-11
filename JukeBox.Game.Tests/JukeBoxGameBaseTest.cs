using NUnit.Framework;
using osu.Framework.Graphics.Performance;

namespace JukeBox.Game.Tests
{
    // Covers JukeBoxGameBase.FrameStatisticsModeFor in isolation from osu.Framework.Game.FrameStatistics
    // itself — see that method's doc comment for why actually flipping the real bindable can't be
    // exercised safely under a headless test host.
    [TestFixture]
    public class JukeBoxGameBaseTest
    {
        [Test]
        public void TrueMapsToFull()
        {
            Assert.That(JukeBoxGameBase.FrameStatisticsModeFor(true), Is.EqualTo(FrameStatisticsMode.Full));
        }

        [Test]
        public void FalseMapsToNone()
        {
            Assert.That(JukeBoxGameBase.FrameStatisticsModeFor(false), Is.EqualTo(FrameStatisticsMode.None));
        }
    }
}
