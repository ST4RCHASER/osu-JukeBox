#nullable enable

using JukeBox.Game.Replays;
using NUnit.Framework;

namespace JukeBox.Game.Tests.MultiReplay
{
    /// <summary>
    /// Fitting the scoreboard on screen. The reported failure was 47 players running off the bottom
    /// of the player box and over the app behind it, so "it fits" is the property under test at
    /// every count rather than at the one that happens to be convenient.
    /// </summary>
    [TestFixture]
    public class RailDensityTest
    {
        private const float typical_height = 700;

        /// <summary>
        /// The whole point. Whatever the count, the rows drawn must not add up to more than the
        /// height there is.
        /// </summary>
        [TestCase(1)]
        [TestCase(4)]
        [TestCase(12)]
        [TestCase(24)]
        [TestCase(47)]
        [TestCase(100)]
        [TestCase(500)]
        public void TheBoardNeverDrawsTallerThanTheSpaceItHas(int players)
        {
            var metrics = RailDensity.For(players, typical_height);

            Assert.That(metrics.VisibleRows * metrics.RowHeight, Is.LessThanOrEqualTo(typical_height),
                $"{players} players overflowed the board");
        }

        /// <summary>
        /// A handful of players looks exactly as it did before this existed — the density scheme is
        /// for the crowded case and must not shrink anything that already fitted.
        /// </summary>
        [Test]
        public void SmallFieldsKeepTheFullSizedRow()
        {
            foreach (int players in new[] { 1, 2, 4, 8 })
            {
                var metrics = RailDensity.For(players, typical_height);

                Assert.That(metrics.RowHeight, Is.EqualTo(RailDensity.MAX_ROW_HEIGHT),
                    $"{players} players should not be shrunk at all");
                Assert.That(metrics.ShowPerformance, Is.True);
                Assert.That(metrics.VisibleRows, Is.EqualTo(players));
            }
        }

        [Test]
        public void RowsShrinkAsTheFieldGrows()
        {
            float twelve = RailDensity.For(12, typical_height).RowHeight;
            float forty = RailDensity.For(40, typical_height).RowHeight;
            float eighty = RailDensity.For(80, typical_height).RowHeight;

            Assert.That(forty, Is.LessThan(twelve));
            Assert.That(eighty, Is.LessThan(forty));
        }

        /// <summary>
        /// Below the floor the text is no longer readable, so shrinking further would only be a way
        /// of claiming to have fitted everyone in.
        /// </summary>
        [Test]
        public void RowsNeverShrinkBelowTheReadableFloor()
        {
            foreach (int players in new[] { 50, 100, 500, 5000 })
            {
                Assert.That(RailDensity.For(players, typical_height).RowHeight,
                    Is.GreaterThanOrEqualTo(RailDensity.MIN_ROW_HEIGHT), $"{players} players");
            }
        }

        /// <summary>
        /// When even the floor will not fit everyone, the surplus is REPORTED rather than quietly
        /// dropped — a player missing from the board with no explanation is worse than a count.
        /// </summary>
        [Test]
        public void PlayersThatCannotFitAreCountedRatherThanDropped()
        {
            // Deliberately absurd: 200 players in a short window cannot all be drawn at 10px.
            var metrics = RailDensity.For(200, 300);

            Assert.That(metrics.VisibleRows, Is.LessThan(200));
            Assert.That(RailDensity.Hidden(200, 300), Is.EqualTo(200 - metrics.VisibleRows));
            Assert.That(RailDensity.Hidden(200, 300), Is.GreaterThan(0));
        }

        [Test]
        public void NothingIsHiddenWhenEveryoneFits()
        {
            Assert.That(RailDensity.Hidden(47, typical_height), Is.Zero);
            Assert.That(RailDensity.Hidden(4, typical_height), Is.Zero);
        }

        /// <summary>The pp column is the first thing to go when rows get tight.</summary>
        [Test]
        public void ThePerformanceColumnGoesBeforeTheRowsBecomeUnreadable()
        {
            Assert.That(RailDensity.For(8, typical_height).ShowPerformance, Is.True);

            // Enough players that the row drops under the pp threshold.
            var tight = RailDensity.For(70, typical_height);

            Assert.That(tight.RowHeight, Is.LessThan(RailDensity.PERFORMANCE_ROW_HEIGHT));
            Assert.That(tight.ShowPerformance, Is.False);
        }

        /// <summary>
        /// The scheme is driven by the height AVAILABLE, not by a constant, so the same field fits a
        /// short window and a tall one differently. A fixed row height is what caused the overflow.
        /// </summary>
        [Test]
        public void TheSameFieldIsDenserInAShorterWindow()
        {
            float tall = RailDensity.For(47, 1400).RowHeight;
            float short_ = RailDensity.For(47, 500).RowHeight;

            Assert.That(short_, Is.LessThan(tall));
            Assert.That(RailDensity.For(47, 500).VisibleRows * short_, Is.LessThanOrEqualTo(500));
        }

        [Test]
        public void FontAndDotFollowTheRowHeight()
        {
            var roomy = RailDensity.For(4, typical_height);
            var tight = RailDensity.For(60, typical_height);

            Assert.That(tight.FontSize, Is.LessThan(roomy.FontSize));
            Assert.That(tight.DotSize, Is.LessThan(roomy.DotSize));
            Assert.That(tight.FontSize, Is.GreaterThanOrEqualTo(7), "still readable");
        }

        [Test]
        public void AnEmptyBoardIsNotADivisionByZero()
        {
            Assert.DoesNotThrow(() => RailDensity.For(0, typical_height));
            Assert.That(RailDensity.For(0, typical_height).VisibleRows, Is.Zero);
        }

        /// <summary>A window with no height yet — the first frame — must not produce nonsense.</summary>
        [Test]
        public void ZeroHeightIsSurvivable()
        {
            var metrics = RailDensity.For(47, 0);

            Assert.That(metrics.RowHeight, Is.GreaterThanOrEqualTo(RailDensity.MIN_ROW_HEIGHT));
            Assert.That(metrics.VisibleRows, Is.GreaterThanOrEqualTo(1));
        }
    }
}
