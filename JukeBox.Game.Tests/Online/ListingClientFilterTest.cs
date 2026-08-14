#nullable enable

using JukeBox.Game.Online;
using JukeBox.Game.UI;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Online
{
    // Covers BeatmapSearchEngine.MatchesClientFilters in isolation: genre/language can't be
    // expressed on the legacy mirror search, so the listing filters already-loaded results
    // client-side — these are the semantics that filtering relies on.
    [TestFixture]
    public class ListingClientFilterTest
    {
        private static BeatmapSetInfo set(int? genreId = null, int? languageId = null) => new BeatmapSetInfo
        {
            Genre = genreId == null ? null : new NamedIdInfo { Id = genreId },
            Language = languageId == null ? null : new NamedIdInfo { Id = languageId },
        };

        [Test]
        public void NoFiltersMatchEverything()
        {
            Assert.That(BeatmapSearchEngine.MatchesClientFilters(set(), null, null), Is.True);
            Assert.That(BeatmapSearchEngine.MatchesClientFilters(set(3, 2), null, null), Is.True);
        }

        [Test]
        public void GenreFilterMatchesOnlySameGenreId()
        {
            Assert.That(BeatmapSearchEngine.MatchesClientFilters(set(genreId: 3), 3, null), Is.True);
            Assert.That(BeatmapSearchEngine.MatchesClientFilters(set(genreId: 4), 3, null), Is.False);
        }

        [Test]
        public void LanguageFilterMatchesOnlySameLanguageId()
        {
            Assert.That(BeatmapSearchEngine.MatchesClientFilters(set(languageId: 2), null, 2), Is.True);
            Assert.That(BeatmapSearchEngine.MatchesClientFilters(set(languageId: 3), null, 2), Is.False);
        }

        [Test]
        public void BothFiltersMustMatch()
        {
            Assert.That(BeatmapSearchEngine.MatchesClientFilters(set(3, 2), 3, 2), Is.True);
            Assert.That(BeatmapSearchEngine.MatchesClientFilters(set(3, 3), 3, 2), Is.False);
            Assert.That(BeatmapSearchEngine.MatchesClientFilters(set(4, 2), 3, 2), Is.False);
        }

        [Test]
        public void UnassignedGenreOrLanguageOnlyPassesAnyFilter()
        {
            // NeriNyan serves {"id":null,"name":null} (or omits the object) for sets that never
            // had a genre/language assigned — they must only show under "Any".
            var missing = new BeatmapSetInfo { Genre = new NamedIdInfo(), Language = null };

            Assert.That(BeatmapSearchEngine.MatchesClientFilters(missing, null, null), Is.True);
            Assert.That(BeatmapSearchEngine.MatchesClientFilters(missing, 3, null), Is.False);
            Assert.That(BeatmapSearchEngine.MatchesClientFilters(missing, null, 2), Is.False);
        }
    }
}
