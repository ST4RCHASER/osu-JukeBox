#nullable enable

using JukeBox.Game.Online;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Online
{
    /// <summary>
    /// Pure parser coverage for what the map-ID dialog accepts — the documented set of link shapes
    /// in <see cref="BeatmapLink"/>, plus the inputs that must be rejected rather than guessed at.
    /// </summary>
    [TestFixture]
    public class BeatmapLinkParseTest
    {
        [TestCase("12345", 12345)]
        [TestCase("  12345  ", 12345)]
        [TestCase("https://osu.ppy.sh/beatmapsets/12345", 12345)]
        [TestCase("https://osu.ppy.sh/beatmapsets/12345/", 12345)]
        [TestCase("http://osu.ppy.sh/beatmapsets/12345", 12345)]
        [TestCase("osu.ppy.sh/beatmapsets/12345", 12345)]
        [TestCase("https://osu.ppy.sh/beatmapsets/12345#osu/67890", 12345)]
        [TestCase("https://osu.ppy.sh/beatmapsets/12345#mania/67890", 12345)]
        [TestCase("https://osu.ppy.sh/beatmapsets/12345/discussion", 12345)]
        [TestCase("https://osu.ppy.sh/beatmapsets/12345?query=x", 12345)]
        [TestCase("https://old.ppy.sh/s/12345", 12345)]
        [TestCase("https://osu.ppy.sh/s/12345", 12345)]
        public void ResolvesToABeatmapSet(string input, int expectedId)
        {
            var link = BeatmapLink.Parse(input);

            Assert.That(link.Kind, Is.EqualTo(BeatmapLinkKind.BeatmapSet));
            Assert.That(link.Id, Is.EqualTo(expectedId));
        }

        // Recognised but deliberately not looked up — no mirror can turn a beatmap id into its
        // set, so the dialog reports this back to the user instead (see BeatmapLinkKind.Beatmap).
        [TestCase("https://osu.ppy.sh/b/67890", 67890)]
        [TestCase("https://osu.ppy.sh/b/67890?m=0", 67890)]
        [TestCase("https://osu.ppy.sh/beatmaps/67890", 67890)]
        [TestCase("osu.ppy.sh/b/67890", 67890)]
        public void ResolvesToASingleDifficulty(string input, int expectedId)
        {
            var link = BeatmapLink.Parse(input);

            Assert.That(link.Kind, Is.EqualTo(BeatmapLinkKind.Beatmap));
            Assert.That(link.Id, Is.EqualTo(expectedId));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("abc")]
        [TestCase("not a map at all")]
        [TestCase("0")]
        [TestCase("-5")]
        [TestCase("12.5")]
        [TestCase("https://osu.ppy.sh/users/12345")]
        [TestCase("https://osu.ppy.sh/beatmapsets")]
        // Another host that happens to have an osu-shaped path must not be mined for an id.
        [TestCase("https://example.com/beatmapsets/12345")]
        public void RejectsAnythingElse(string? input)
        {
            Assert.That(BeatmapLink.Parse(input).Kind, Is.EqualTo(BeatmapLinkKind.Invalid));
        }
    }
}
