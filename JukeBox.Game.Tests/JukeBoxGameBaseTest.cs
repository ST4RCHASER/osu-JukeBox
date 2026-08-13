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

        // Compact no longer drives the framework overlay at all — it's JukeBoxGameBase's own
        // FpsCounter drawable instead (see the overlay-visibility tests below).
        [Test]
        public void CompactMapsToNone()
        {
            Assert.That(JukeBoxGameBase.FrameStatisticsModeFor(FpsDisplayMode.Compact), Is.EqualTo(FrameStatisticsMode.None));
        }

        [Test]
        public void DetailsMapsToMinimal()
        {
            Assert.That(JukeBoxGameBase.FrameStatisticsModeFor(FpsDisplayMode.Details), Is.EqualTo(FrameStatisticsMode.Minimal));
        }

        [Test]
        public void GraphMapsToFull()
        {
            Assert.That(JukeBoxGameBase.FrameStatisticsModeFor(FpsDisplayMode.Graph), Is.EqualTo(FrameStatisticsMode.Full));
        }

        // Covers the one-shot ShowFps -> FpsDisplay (legacy) migration mapping (JukeBoxGameBase.load)
        // in isolation from the config manager itself. Unchanged by the Compact-overlay/Graph
        // rename — still lands in the legacy shape, one hop before MigrateLegacyFpsDisplay takes it
        // the rest of the way (covered separately below).
        [Test]
        public void MigrateShowFpsTrueMapsToLegacyDetails()
        {
            Assert.That(JukeBoxGameBase.MigrateShowFps(true), Is.EqualTo(LegacyFpsDisplayMode.Details));
        }

        [Test]
        public void MigrateShowFpsFalseMapsToLegacyOff()
        {
            Assert.That(JukeBoxGameBase.MigrateShowFps(false), Is.EqualTo(LegacyFpsDisplayMode.Off));
        }

        // Covers the one-shot FpsDisplay (legacy) -> FpsDisplayMode migration mapping
        // (JukeBoxGameBase.load) in isolation from the config manager itself: the legacy enum
        // reuses "Compact"/"Details" as NAMES for different meanings, so this is an explicit
        // value-by-value remap rather than a straight Enum.Parse of the old text.
        [Test]
        public void MigrateLegacyFpsDisplayOffMapsToOff()
        {
            Assert.That(JukeBoxGameBase.MigrateLegacyFpsDisplay(LegacyFpsDisplayMode.Off), Is.EqualTo(FpsDisplayMode.Off));
        }

        [Test]
        public void MigrateLegacyFpsDisplayCompactMapsToDetails()
        {
            Assert.That(JukeBoxGameBase.MigrateLegacyFpsDisplay(LegacyFpsDisplayMode.Compact), Is.EqualTo(FpsDisplayMode.Details));
        }

        [Test]
        public void MigrateLegacyFpsDisplayDetailsMapsToGraph()
        {
            Assert.That(JukeBoxGameBase.MigrateLegacyFpsDisplay(LegacyFpsDisplayMode.Details), Is.EqualTo(FpsDisplayMode.Graph));
        }

        // Unrecognised legacy values (e.g. a future enum member, or a raw cast from garbage) must
        // fall back to Off, same as the framework's own catch-and-default behaviour for ini text
        // that fails to parse at all.
        [Test]
        public void MigrateLegacyFpsDisplayUnrecognisedValueMapsToOff()
        {
            Assert.That(JukeBoxGameBase.MigrateLegacyFpsDisplay((LegacyFpsDisplayMode)999), Is.EqualTo(FpsDisplayMode.Off));
        }
    }
}
