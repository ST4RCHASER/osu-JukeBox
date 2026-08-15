#nullable enable

using JukeBox.Game.Import;
using NUnit.Framework;

namespace JukeBox.Game.Tests.Import
{
    [TestFixture]
    public class DroppedFileTest
    {
        [TestCase("/tmp/map.osz", DroppedFileKind.BeatmapArchive)]
        [TestCase("/tmp/map.OSZ", DroppedFileKind.BeatmapArchive)]
        [TestCase("/tmp/skin.osk", DroppedFileKind.SkinArchive)]
        [TestCase("/tmp/skin.Osk", DroppedFileKind.SkinArchive)]
        [TestCase("/tmp/play.osr", DroppedFileKind.Replay)]
        [TestCase("/tmp/PLAY.OSR", DroppedFileKind.Replay)]
        public void RecognisedExtensionsDispatchByKind(string path, DroppedFileKind expected)
            => Assert.That(DroppedFile.Classify(path), Is.EqualTo(expected));

        [TestCase("/tmp/song.mp3")]
        [TestCase("/tmp/archive.zip")]
        [TestCase("/tmp/no-extension")]
        [TestCase("")]
        // A directory drop (osu!'s own formats are all files) and a name that merely CONTAINS a
        // known extension must both fall through rather than be mistaken for an archive.
        [TestCase("/tmp/some.osz.txt")]
        [TestCase("/tmp/folder")]
        public void EverythingElseIsUnsupported(string path)
            => Assert.That(DroppedFile.Classify(path), Is.EqualTo(DroppedFileKind.Unsupported));
    }
}
