using System.IO;
using JukeBox.Game.Online;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Online
{
    [TestFixture]
    public class BeatmapSetInfoParsingTest
    {
        [Test]
        public void ParsesNerinyanSearchArray()
        {
            string json = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "nerinyan_search.json"));
            var sets = BeatmapSetInfo.ParseList(json);
            Assert.That(sets, Is.Not.Empty);
            Assert.That(sets[0].Id, Is.GreaterThan(0));
            Assert.That(sets[0].Title, Is.Not.Empty);
            Assert.That(sets[0].Beatmaps, Is.Not.Empty);
            Assert.That(sets[0].Beatmaps[0].Version, Is.Not.Empty);
        }

        [Test]
        public void DisplayTitleAndArtistPreferRomanizedOverUnicode()
        {
            var set = new BeatmapSetInfo { Title = "Romanized Title", TitleUnicode = "曲名", Artist = "Romanized Artist", ArtistUnicode = "アーティスト" };
            Assert.That(set.DisplayTitle, Is.EqualTo("Romanized Title"));
            Assert.That(set.DisplayArtist, Is.EqualTo("Romanized Artist"));
        }

        [Test]
        public void DisplayTitleAndArtistFallBackToUnicodeWhenRomanizedMissing()
        {
            var set = new BeatmapSetInfo { Title = "", TitleUnicode = "曲名", Artist = "", ArtistUnicode = "アーティスト" };
            Assert.That(set.DisplayTitle, Is.EqualTo("曲名"));
            Assert.That(set.DisplayArtist, Is.EqualTo("アーティスト"));
        }
    }
}
