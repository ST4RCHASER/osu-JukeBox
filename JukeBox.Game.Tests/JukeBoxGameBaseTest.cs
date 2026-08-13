using JukeBox.Game.Configuration;
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
        public void OffMapsToNone()
        {
            Assert.That(JukeBoxGameBase.FrameStatisticsModeFor(FpsDisplayMode.Off), Is.EqualTo(FrameStatisticsMode.None));
        }

        [Test]
        public void CompactMapsToMinimal()
        {
            Assert.That(JukeBoxGameBase.FrameStatisticsModeFor(FpsDisplayMode.Compact), Is.EqualTo(FrameStatisticsMode.Minimal));
        }

        [Test]
        public void DetailsMapsToFull()
        {
            Assert.That(JukeBoxGameBase.FrameStatisticsModeFor(FpsDisplayMode.Details), Is.EqualTo(FrameStatisticsMode.Full));
        }

        // Covers the one-shot ShowFps -> FpsDisplay migration mapping (JukeBoxGameBase.load) in
        // isolation from the config manager itself.
        [Test]
        public void MigrateShowFpsTrueMapsToDetails()
        {
            Assert.That(JukeBoxGameBase.MigrateShowFps(true), Is.EqualTo(FpsDisplayMode.Details));
        }

        [Test]
        public void MigrateShowFpsFalseMapsToOff()
        {
            Assert.That(JukeBoxGameBase.MigrateShowFps(false), Is.EqualTo(FpsDisplayMode.Off));
        }
    }
}
