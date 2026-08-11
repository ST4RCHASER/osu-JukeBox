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
    }
}
