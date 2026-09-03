#nullable enable

using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Online;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Online
{
    /// <summary>The watch list's edit rules and its round trip through the config file.</summary>
    [TestFixture]
    public class SpectateWatchListTest
    {
        [Test]
        public void NamesSurviveARoundTripThroughTheStoredString()
        {
            var names = new[] { "mrekk", "chocomint", "peppy" };

            Assert.That(SpectateWatchList.Parse(SpectateWatchList.Format(names)), Is.EqualTo(names));
        }

        [Test]
        public void AnEmptyOrWhitespaceStoredValueReadsAsNobodyWatched()
        {
            Assert.That(SpectateWatchList.Parse(null), Is.Empty);
            Assert.That(SpectateWatchList.Parse(string.Empty), Is.Empty);
            Assert.That(SpectateWatchList.Parse("   "), Is.Empty);
        }

        [Test]
        public void ParsingSurvivesAHandEditedFileWithBlanksAndPadding()
        {
            // The value is a plain string in game.ini, so a person can and will edit it.
            Assert.That(SpectateWatchList.Parse(" mrekk , ,peppy,, "), Is.EqualTo(new[] { "mrekk", "peppy" }));
        }

        [Test]
        public void ParsingCannotProduceMoreNamesThanTheCapAllows()
        {
            string overfull = string.Join(',', Enumerable.Range(0, SpectateWatchList.MAX_WATCHED + 5).Select(i => $"player{i}"));

            Assert.That(SpectateWatchList.Parse(overfull).Count, Is.EqualTo(SpectateWatchList.MAX_WATCHED));
        }

        [Test]
        public void AddingSomeoneAlreadyWatchedInADifferentCaseChangesNothing()
        {
            IReadOnlyList<string> list = new[] { "mrekk" };

            var after = SpectateWatchList.Add(list, "MREKK");

            // osu! usernames are case-insensitive, so this would otherwise spend two of eight slots
            // watching one person twice.
            Assert.That(after, Is.EqualTo(list));
        }

        [Test]
        public void AddingTrimsSurroundingSpaceRatherThanStoringIt()
        {
            var after = SpectateWatchList.Add(System.Array.Empty<string>(), "  mrekk  ");

            Assert.That(after, Is.EqualTo(new[] { "mrekk" }));
        }

        [Test]
        public void ANameContainingTheSeparatorIsRefusedRatherThanSplitLater()
        {
            var after = SpectateWatchList.Add(System.Array.Empty<string>(), "mrekk,peppy");

            // Accepting it would store one name that read back as two on the next launch.
            Assert.That(after, Is.Empty);
        }

        [Test]
        public void AddingToAFullListLeavesItUnchanged()
        {
            var full = Enumerable.Range(0, SpectateWatchList.MAX_WATCHED).Select(i => $"player{i}").ToList();

            var after = SpectateWatchList.Add(full, "onemore");

            Assert.That(after.Count, Is.EqualTo(SpectateWatchList.MAX_WATCHED));
            Assert.That(after, Does.Not.Contain("onemore"));
        }

        [Test]
        public void AddingReturnsANewListSoCallersCanTellWhetherAnythingHappened()
        {
            IReadOnlyList<string> list = new[] { "mrekk" };

            // The UI decides between clearing the text box and leaving it alone on exactly this.
            Assert.That(SpectateWatchList.Add(list, "peppy").Count, Is.EqualTo(2));
            Assert.That(list.Count, Is.EqualTo(1), "the original list must not have been mutated");
        }

        [Test]
        public void RemovingIgnoresCaseTheSameWayAddingDoes()
        {
            IReadOnlyList<string> list = new[] { "mrekk", "peppy" };

            Assert.That(SpectateWatchList.Remove(list, "MREKK"), Is.EqualTo(new[] { "peppy" }));
        }

        [Test]
        public void ContainsAnswersForANameTypedWithPadding()
        {
            IReadOnlyList<string> list = new[] { "mrekk" };

            Assert.That(SpectateWatchList.Contains(list, " Mrekk "), Is.True);
            Assert.That(SpectateWatchList.Contains(list, "peppy"), Is.False);
        }
    }
}
