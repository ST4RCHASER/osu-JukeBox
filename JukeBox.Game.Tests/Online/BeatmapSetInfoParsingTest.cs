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

            // The fixture's genre/language are the null-id/null-name shape NeriNyan serves for
            // sets without one assigned — must deserialize (not throw), with null members.
            Assert.That(sets[0].Genre, Is.Not.Null);
            Assert.That(sets[0].Genre!.Id, Is.Null);
            Assert.That(sets[0].Language, Is.Not.Null);
            Assert.That(sets[0].Bpm, Is.GreaterThan(0));
            Assert.That(sets[0].RankedDate, Is.Not.Null);
        }

        [Test]
        public void ParsesPopulatedGenreAndLanguage()
        {
            const string json = """
                [{"id":42,"title":"t","artist":"a","creator":"c","bpm":180.5,
                  "genre":{"id":3,"name":"Anime"},"language":{"id":2,"name":"English"},
                  "ranked_date":"2020-01-02T03:04:05Z","beatmaps":[]}]
                """;

            var sets = BeatmapSetInfo.ParseList(json);

            Assert.That(sets[0].Genre!.Id, Is.EqualTo(3));
            Assert.That(sets[0].Genre!.Name, Is.EqualTo("Anime"));
            Assert.That(sets[0].Language!.Id, Is.EqualTo(2));
            Assert.That(sets[0].Language!.Name, Is.EqualTo("English"));
            Assert.That(sets[0].Bpm, Is.EqualTo(180.5));
            Assert.That(sets[0].RankedDate!.Value.Year, Is.EqualTo(2020));
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
