#nullable enable

using System;
using JukeBox.Game.Online;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Online
{
    /// <summary>
    /// The replay-download allowance. Every test drives an explicit clock, because the whole point
    /// of the type is what it does as time passes and none of that is observable otherwise.
    /// </summary>
    [TestFixture]
    public class ReplayDownloadBudgetTest
    {
        private static readonly DateTimeOffset start = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

        [Test]
        public void TheTenthDownloadIsAllowedAndTheEleventhIsNot()
        {
            var budget = new ReplayDownloadBudget();

            for (int i = 0; i < ReplayDownloadBudget.MAX_PER_WINDOW; i++)
                Assert.That(budget.TryTake(start), Is.True, $"download {i + 1} should have been allowed");

            Assert.That(budget.TryTake(start), Is.False);
        }

        [Test]
        public void SpentDownloadsComeBackOneAtATimeAsTheirOwnMinuteLapses()
        {
            var budget = new ReplayDownloadBudget();

            // Spent five seconds apart — all inside one window, so they expire five seconds apart
            // too. A fixed window would instead hand all ten back at once at the minute boundary,
            // which is the burst this type exists to prevent.
            for (int i = 0; i < ReplayDownloadBudget.MAX_PER_WINDOW; i++)
                budget.TryTake(start + TimeSpan.FromSeconds(i * 5));

            Assert.That(budget.Remaining(start + TimeSpan.FromSeconds(59)), Is.Zero);

            // A second past the first one's minute: exactly one has come back, not ten.
            Assert.That(budget.Remaining(start + TimeSpan.FromSeconds(61)), Is.EqualTo(1));
            Assert.That(budget.Remaining(start + TimeSpan.FromSeconds(66)), Is.EqualTo(2));
        }

        [Test]
        public void AWholeWindowOfSilenceRestoresTheFullAllowance()
        {
            var budget = new ReplayDownloadBudget();

            for (int i = 0; i < ReplayDownloadBudget.MAX_PER_WINDOW; i++)
                budget.TryTake(start);

            var later = start + ReplayDownloadBudget.WINDOW;

            Assert.That(budget.Remaining(later), Is.EqualTo(ReplayDownloadBudget.MAX_PER_WINDOW));
            Assert.That(budget.TryTake(later), Is.True);
        }

        [Test]
        public void A429StopsDownloadsForLongerThanTheWindowItself()
        {
            var budget = new ReplayDownloadBudget();

            budget.Throttled(start);

            Assert.That(budget.TryTake(start), Is.False);
            Assert.That(budget.IsThrottled(start), Is.True);

            // Still refused a whole window later — the backoff deliberately outlasts our own
            // accounting, which the 429 proved wrong.
            Assert.That(budget.TryTake(start + ReplayDownloadBudget.WINDOW), Is.False);

            Assert.That(budget.TryTake(start + ReplayDownloadBudget.THROTTLE_BACKOFF), Is.True);
        }

        [Test]
        public void RemainingReportsNothingWhileThrottledEvenWithAnUnspentAllowance()
        {
            var budget = new ReplayDownloadBudget();

            // Nothing has been spent, so the tally alone would say ten are available. The throttle
            // has to outrank it, or the UI would tell a waiting user they are free to download.
            budget.Throttled(start);

            Assert.That(budget.Remaining(start), Is.Zero);
        }
    }
}
