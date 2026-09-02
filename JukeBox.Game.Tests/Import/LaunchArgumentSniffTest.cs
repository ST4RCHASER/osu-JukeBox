#nullable enable

using System.IO;
using System.IO.Compression;
using JukeBox.Game.Import;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Import
{
    /// <summary>
    /// Telling downloaded content apart when its NAME says nothing. This is not a corner case:
    /// mirror download links routinely carry no extension at all (catboy.best/d/1234), so without
    /// this a perfectly good beatmap arrives as "couldn't tell what this is".
    /// </summary>
    [TestFixture]
    public class LaunchArgumentSniffTest
    {
        private string tmp = null!;

        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, true);
        }

        // .osz and .osk are both zips, so only the contents separate them.
        [Test]
        public void AZipCarryingDifficultiesIsABeatmapArchive()
        {
            string path = zip(("map.osu", "osu file format v14\n"), ("audio.mp3", "x"));

            Assert.That(LaunchArgumentImporter.SniffExtension(path), Is.EqualTo(".osz"));
        }

        [Test]
        public void AZipDeclaringASkinIsASkinArchive()
        {
            string path = zip(("skin.ini", "[General]\nName: Aristia\n"), ("cursor.png", "x"));

            Assert.That(LaunchArgumentImporter.SniffExtension(path), Is.EqualTo(".osk"));
        }

        // A beatmap archive also contains images and audio; the .osu files are what decide it, and
        // they must win over anything else present.
        [Test]
        public void ABeatmapArchiveThatAlsoLooksSkinnyIsStillABeatmap()
        {
            string path = zip(("map.osu", "osu file format v14\n"), ("skin.ini", "[General]\n"));

            Assert.That(LaunchArgumentImporter.SniffExtension(path), Is.EqualTo(".osz"));
        }

        // Confirmed by really parsing the header with the same reader the drag-and-drop path uses,
        // rather than by guessing at a signature.
        [Test]
        public void AReplayIsRecognisedByItsHeader()
        {
            string beatmap = Path.Combine(tmp, "map.osu");
            File.WriteAllText(beatmap, "osu file format v14\n");

            string path = Path.Combine(tmp, "no-extension-here");
            ReplayFixture.Write(path, beatmap, "Someone");

            Assert.That(LaunchArgumentImporter.SniffExtension(path), Is.EqualTo(".osr"));
        }

        [Test]
        public void AZipOfSomethingElseEntirelyIsNotImportable()
        {
            string path = zip(("notes.txt", "hello"), ("photo.png", "x"));

            Assert.That(LaunchArgumentImporter.SniffExtension(path), Is.Empty);
        }

        [TestCase("just some text, not an archive at all")]
        [TestCase("")]
        public void SomethingThatIsNeitherIsNotImportable(string content)
        {
            string path = Path.Combine(tmp, Path.GetRandomFileName());
            File.WriteAllText(path, content);

            Assert.That(LaunchArgumentImporter.SniffExtension(path), Is.Empty);
        }

        private string zip(params (string Name, string Content)[] entries)
        {
            string build = Path.Combine(tmp, "build-" + Path.GetRandomFileName());
            Directory.CreateDirectory(build);

            foreach ((string name, string content) in entries)
                File.WriteAllText(Path.Combine(build, name), content);

            string path = Path.Combine(tmp, Path.GetRandomFileName());
            ZipFile.CreateFromDirectory(build, path);
            return path;
        }
    }
}
