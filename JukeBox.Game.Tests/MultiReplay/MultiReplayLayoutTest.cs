#nullable enable

using System.Linq;
using JukeBox.Game.Replays;
using NUnit.Framework;

// Deliberately NOT JukeBox.Game.Tests.Replays: that name shadows JukeBox.Game.Replays for any test
// file that qualifies a type as "Replays.Something", which breaks their compile from a distance.
namespace JukeBox.Game.Tests.MultiReplay
{
    /// <summary>
    /// The arithmetic of watching several replays at once, decided before any drawable exists.
    /// </summary>
    [TestFixture]
    public class MultiReplayLayoutTest
    {
        /// <summary>
        /// Columns before rows, because the player area is far wider than it is tall. Eight is the
        /// case that pins it: a square-root split would give 3x3 and waste the width, where the
        /// reference material (and this) uses 4x2.
        /// </summary>
        [Test]
        public void TheGridFillsTheWidthBeforeItAddsRows()
        {
            Assert.That(MultiReplayLayout.For(1), Is.EqualTo(new GridShape(1, 1)));
            Assert.That(MultiReplayLayout.For(2), Is.EqualTo(new GridShape(2, 1)));
            Assert.That(MultiReplayLayout.For(3), Is.EqualTo(new GridShape(3, 1)));
            Assert.That(MultiReplayLayout.For(4), Is.EqualTo(new GridShape(2, 2)));
            Assert.That(MultiReplayLayout.For(6), Is.EqualTo(new GridShape(3, 2)));
            Assert.That(MultiReplayLayout.For(8), Is.EqualTo(new GridShape(4, 2)), "the reference layout");
            Assert.That(MultiReplayLayout.For(9), Is.EqualTo(new GridShape(3, 3)));
            Assert.That(MultiReplayLayout.For(12), Is.EqualTo(new GridShape(4, 3)));
        }

        [Test]
        public void EveryShapeHasEnoughCellsForItsReplays()
        {
            for (int n = 1; n <= MultiReplayLayout.MAX_GRID_CELLS; n++)
                Assert.That(MultiReplayLayout.For(n).Cells, Is.GreaterThanOrEqualTo(n), $"{n} replays");
        }

        /// <summary>
        /// Each cell is a whole gameplay renderer, so the cap is a real budget. Replays past it keep
        /// their credit but get no cell — the grid must not silently try to render fifty.
        /// </summary>
        [Test]
        public void TheGridStopsAtItsCap()
        {
            Assert.That(MultiReplayLayout.RenderedCount(8), Is.EqualTo(8));
            Assert.That(MultiReplayLayout.RenderedCount(MultiReplayLayout.MAX_GRID_CELLS), Is.EqualTo(MultiReplayLayout.MAX_GRID_CELLS));
            Assert.That(MultiReplayLayout.RenderedCount(45), Is.EqualTo(MultiReplayLayout.MAX_GRID_CELLS));

            Assert.That(MultiReplayLayout.For(45).Cells, Is.LessThanOrEqualTo(MultiReplayLayout.MAX_GRID_CELLS),
                "and the shape never exceeds it either");
        }

        [Test]
        public void NoReplaysStillAsksForOneCellRatherThanZero()
        {
            Assert.That(MultiReplayLayout.For(0), Is.EqualTo(new GridShape(1, 1)));
            Assert.That(MultiReplayLayout.RenderedCount(0), Is.Zero);
        }

        // ---- the rate rule ----

        private static ReplayAttachment at(double tempo = 1, double frequency = 1, string player = "someone")
            => new ReplayAttachment { PlayerName = player, RateTempo = tempo, RateFrequency = frequency };

        [Test]
        public void ReplaysPlayedAtTheSameSpeedShareAClockHappily()
        {
            var replays = new[] { at(), at(), at() };

            Assert.That(MultiReplayLayout.RatesAgree(replays), Is.True);
            Assert.That(MultiReplayLayout.SharedRate(replays), Is.EqualTo(1));
        }

        /// <summary>
        /// The one incompatibility a shared clock cannot paper over: a DoubleTime play and a no-mod
        /// play of the same map are different LENGTHS, so no single clock drives both correctly.
        /// Visual mods differ per cell quite happily; speed cannot, because there is one audio track.
        /// </summary>
        [Test]
        public void MixedSpeedsPlayAtTheMapsOwnSpeed()
        {
            var replays = new[] { at(player: "nomod"), at(tempo: 1.5, player: "DT") };

            Assert.That(MultiReplayLayout.RatesAgree(replays), Is.False);
            Assert.That(MultiReplayLayout.SharedRate(replays), Is.EqualTo(1));
        }

        /// <summary>
        /// Drop order makes no difference any more: a DoubleTime play leading the list used to drag
        /// the whole session to 1.5x.
        /// </summary>
        [Test]
        public void MixedSpeedsPlayAtOneEvenWhenTheFastReplayIsFirst()
        {
            var dtFirst = new[] { at(tempo: 1.5, player: "DT"), at(player: "nomod") };

            Assert.That(MultiReplayLayout.SharedRate(dtFirst), Is.EqualTo(1));
        }

        /// <summary>An AGREED non-1x speed is still honoured: two DoubleTime plays watch at 1.5x,
        /// the speed both were actually played at.</summary>
        [Test]
        public void AnAgreedSpeedIsStillTheSharedSpeed()
        {
            var bothDt = new[] { at(tempo: 1.5, player: "DT"), at(tempo: 1.5, player: "also DT") };

            Assert.That(MultiReplayLayout.RatesAgree(bothDt), Is.True);
            Assert.That(MultiReplayLayout.SharedRate(bothDt), Is.EqualTo(1.5));
        }

        /// <summary>
        /// Rate is a product of two doubles, so equal speeds do not always compare equal — half-time
        /// as 0.75 arrived at two ways must still count as one speed.
        /// </summary>
        [Test]
        public void NearlyIdenticalRatesCountAsOneSpeed()
        {
            var replays = new[] { at(tempo: 1.5), at(tempo: 1.5 + 1e-9) };

            Assert.That(MultiReplayLayout.RatesAgree(replays), Is.True);
            Assert.That(MultiReplayLayout.DistinctRates(replays), Has.Count.EqualTo(1));
        }

        [Test]
        public void EverySpeedPresentIsReportedInFirstSeenOrder()
        {
            var replays = new[] { at(player: "a"), at(tempo: 1.5, player: "b"), at(player: "c"), at(tempo: 0.75, player: "d") };

            Assert.That(MultiReplayLayout.DistinctRates(replays).ToArray(), Is.EqualTo(new[] { 1d, 1.5d, 0.75d }));
        }

        [Test]
        public void OneReplayAndNoReplaysBothAgreeWithThemselves()
        {
            Assert.That(MultiReplayLayout.RatesAgree(new[] { at(tempo: 1.5) }), Is.True);
            Assert.That(MultiReplayLayout.RatesAgree(System.Array.Empty<ReplayAttachment>()), Is.True);
            Assert.That(MultiReplayLayout.SharedRate(System.Array.Empty<ReplayAttachment>()), Is.EqualTo(1));
        }
    }
}
