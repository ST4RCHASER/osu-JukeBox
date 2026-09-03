#nullable enable

using System;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Replays;
using NUnit.Framework;

namespace JukeBox.Game.Tests.MultiReplay
{
    /// <summary>
    /// The per-frame budget the simulation spends is chosen from how much LEAD the slowest play has
    /// on the playhead. The 47-real-replays freeze is the case this exists for: when the lead runs
    /// out the budget has to jump so the board does not stop counting, and this pins that escalation
    /// without needing a clock or a render.
    /// </summary>
    [TestFixture]
    public class ReplaySimulatorBudgetTest
    {
        private static ReplaySimulator sim() => new ReplaySimulator("x", Array.Empty<ReplayAttachment>());

        [Test]
        public void FallingBehindOrNearlySoSpendsTheEmergencyBudget()
        {
            var s = sim();

            // Behind the playhead entirely — the board is frozen unless this claws back fast.
            Assert.That(s.BudgetFor(-2000), Is.EqualTo(s.EmergencyBudgetMs));

            // Still ahead, but the lead is nearly gone (< the 5s emergency cushion).
            Assert.That(s.BudgetFor(2000), Is.EqualTo(s.EmergencyBudgetMs));
        }

        [Test]
        public void AHealthyButUnbuiltCushionSpendsTheCatchUpBudget()
        {
            var s = sim();

            // Comfortably ahead of the emergency line, but short of the full look-ahead cushion.
            Assert.That(s.BudgetFor(10_000), Is.EqualTo(s.CatchUpBudgetMs));
        }

        [Test]
        public void WellAheadSpendsOnlyTheIdleBudget()
        {
            var s = sim();

            // Past the whole look-ahead cushion — nothing urgent, so trickle.
            Assert.That(s.BudgetFor(40_000), Is.EqualTo(s.BudgetMs));
        }

        [Test]
        public void TheTiersEscalateStrictly()
        {
            var s = sim();

            // The whole point: falling behind must buy MORE time than building the cushion, which in
            // turn buys more than idling. A flat budget is what froze the board.
            Assert.That(s.EmergencyBudgetMs, Is.GreaterThan(s.CatchUpBudgetMs));
            Assert.That(s.CatchUpBudgetMs, Is.GreaterThan(s.BudgetMs));
        }
    }
}
