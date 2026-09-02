#nullable enable

using JukeBox.Game.Import;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Import
{
    /// <summary>
    /// What each shape of command-line argument means. Pure classification — no disk, no network —
    /// so every case here is exact rather than environment-dependent.
    /// </summary>
    [TestFixture]
    public class LaunchArgumentTest
    {
        // The modern set page, the legacy /s/ form, and a bare id all mean the same thing: queue
        // this set. Scheme and www. are optional because a pasted link may carry either.
        [TestCase("https://osu.ppy.sh/beatmapsets/12345", 12345)]
        [TestCase("http://osu.ppy.sh/beatmapsets/12345", 12345)]
        [TestCase("https://www.osu.ppy.sh/beatmapsets/12345/", 12345)]
        [TestCase("https://osu.ppy.sh/s/12345", 12345)]
        [TestCase("https://osu.ppy.sh/beatmapsets/12345?mode=osu", 12345)]
        public void SetLinksResolveToTheirSet(string argument, int expected)
        {
            var parsed = LaunchArgument.Classify(argument);

            Assert.That(parsed.Kind, Is.EqualTo(LaunchArgumentKind.BeatmapSet));
            Assert.That(parsed.Id, Is.EqualTo(expected));
            Assert.That(parsed.DifficultyId, Is.Zero, "no difficulty was named");
        }

        [TestCase("12345", 12345)]
        [TestCase("  12345  ", 12345)]
        public void ABareNumberIsASetId(string argument, int expected)
        {
            var parsed = LaunchArgument.Classify(argument);

            Assert.That(parsed.Kind, Is.EqualTo(LaunchArgumentKind.BeatmapSet));
            Assert.That(parsed.Id, Is.EqualTo(expected));
        }

        // A deep link still queues the SET — that is the downloadable unit — but remembers which
        // difficulty was asked for, so the player can open on it.
        [TestCase("https://osu.ppy.sh/beatmapsets/12345#osu/67890", 12345, 67890)]
        [TestCase("https://osu.ppy.sh/beatmapsets/12345#mania/67890", 12345, 67890)]
        [TestCase("https://osu.ppy.sh/beatmapsets/12345#fruits/1", 12345, 1)]
        public void ADeepLinkKeepsTheSetAndRemembersTheDifficulty(string argument, int expectedSet, int expectedDifficulty)
        {
            var parsed = LaunchArgument.Classify(argument);

            Assert.That(parsed.Kind, Is.EqualTo(LaunchArgumentKind.BeatmapSet));
            Assert.That(parsed.Id, Is.EqualTo(expectedSet));
            Assert.That(parsed.DifficultyId, Is.EqualTo(expectedDifficulty));
        }

        // A difficulty link names a beatmap, not a set. The mirrors cannot turn one into the other
        // — only osu!'s own API can — so this is its own kind rather than being lumped in with the
        // sets it cannot yet be resolved to.
        [TestCase("https://osu.ppy.sh/b/67890", 67890)]
        [TestCase("https://osu.ppy.sh/beatmaps/67890", 67890)]
        public void DifficultyLinksAreTheirOwnKind(string argument, int expected)
        {
            var parsed = LaunchArgument.Classify(argument);

            Assert.That(parsed.Kind, Is.EqualTo(LaunchArgumentKind.Beatmap));
            Assert.That(parsed.Id, Is.EqualTo(expected));
        }

        // Any other http(s) URL is something to fetch. No extension is required: mirror download
        // links routinely have none (catboy.best/d/1234), so what arrived is classified after the
        // download, not before it.
        [TestCase("https://example.com/skins/aristia.osk")]
        [TestCase("https://catboy.best/d/1234")]
        [TestCase("https://example.com/replays/play.osr?token=abc")]
        public void OtherWebUrlsAreDownloaded(string argument)
        {
            var parsed = LaunchArgument.Classify(argument);

            Assert.That(parsed.Kind, Is.EqualTo(LaunchArgumentKind.RemoteFile));
            Assert.That(parsed.Path, Is.EqualTo(argument));
        }

        // A link on a host that merely looks osu-shaped must not resolve to an unrelated id.
        [Test]
        public void AnOsuShapedPathOnAnotherHostIsJustAUrl()
        {
            var parsed = LaunchArgument.Classify("https://evil.example/beatmapsets/12345");

            Assert.That(parsed.Kind, Is.EqualTo(LaunchArgumentKind.RemoteFile));
        }

        [TestCase("/Users/someone/maps/song.osz", ".osz")]
        [TestCase("song.osz", ".osz")]
        [TestCase("skins/Aristia.osk", ".osk")]
        [TestCase("./replays/play.osr", ".osr")]
        [TestCase("/Users/someone/My Maps/a song.osz", ".osz")]
        [TestCase(@"C:\Users\someone\song.osz", ".osz")]
        public void LocalPathsAreImportedFromDisk(string argument, string _)
        {
            var parsed = LaunchArgument.Classify(argument);

            Assert.That(parsed.Kind, Is.EqualTo(LaunchArgumentKind.LocalFile));
            Assert.That(parsed.Path, Is.EqualTo(argument));
        }

        // file:// is a URL by syntax and a path by meaning. Uri is what decodes its escapes, so a
        // path with spaces survives the round trip.
        [Test]
        public void FileUrlsBecomeLocalPaths()
        {
            var parsed = LaunchArgument.Classify("file:///Users/someone/My%20Maps/a%20song.osz");

            Assert.That(parsed.Kind, Is.EqualTo(LaunchArgumentKind.LocalFile));
            Assert.That(parsed.Path, Is.EqualTo("/Users/someone/My Maps/a song.osz"));
        }

        // A path that does not exist still classifies as a file: it fails later with "no such
        // file", which says far more than "unsupported argument" would.
        [Test]
        public void ClassificationNeverTouchesTheDisk()
        {
            var parsed = LaunchArgument.Classify("/no/such/directory/nothing-here.osz");

            Assert.That(parsed.Kind, Is.EqualTo(LaunchArgumentKind.LocalFile));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("hello")]
        [TestCase("song.mp3")]
        [TestCase("/Users/someone/notes.txt")]
        [TestCase("0")]
        [TestCase("-5")]
        public void AnythingElseIsUnsupported(string? argument)
        {
            Assert.That(LaunchArgument.Classify(argument).Kind, Is.EqualTo(LaunchArgumentKind.Unsupported));
        }

        // The app's own switches are not content. They are filtered before classification, and
        // classifying one anyway must never produce something queueable.
        [TestCase("--viewer")]
        [TestCase("-v")]
        public void SwitchesAreNotContent(string argument)
        {
            Assert.That(LaunchArgument.IsSwitch(argument), Is.True);
            Assert.That(LaunchArgument.Classify(argument).Kind, Is.EqualTo(LaunchArgumentKind.Unsupported));
        }

        [Test]
        public void TheRawArgumentIsKeptForErrorMessages()
        {
            Assert.That(LaunchArgument.Classify("  hello  ").Raw, Is.EqualTo("hello"));
            Assert.That(LaunchArgument.Classify("https://osu.ppy.sh/s/7").Raw, Is.EqualTo("https://osu.ppy.sh/s/7"));
        }
    }
}
