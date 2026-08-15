#nullable enable

using System.IO;
using JukeBox.Game.LazerPlayer;
using NUnit.Framework;

namespace JukeBox.Game.Tests.LazerPlayer
{
    /// <summary>
    /// The beatmap folder's own file lookup, which serves storyboard sprites and the video stream.
    ///
    /// <para>
    /// The case that brought this here: osu!'s submission system re-encodes uploaded videos, so a
    /// map that shipped an .avi is served as .mp4 — while its .osu keeps referencing the ORIGINAL
    /// name. Set 683417 is exactly that (its [Events] says "…MV.avi"; the .osz contains "…MV.mp4"),
    /// and a lookup that only tries the literal reference finds nothing.
    /// </para>
    /// </summary>
    [TestFixture]
    public class BeatmapFolderResourceStoreTest
    {
        private string dir = null!;

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
        }

        // NOTE: deliberately not deleting `dir` — same convention as the other store fixtures here.

        private void write(string name, string content = "x") => File.WriteAllText(Path.Combine(dir, name), content);

        private LazerStoryboardLayer.BeatmapFolderResourceStore store() => new LazerStoryboardLayer.BeatmapFolderResourceStore(dir);

        [Test]
        public void AnExactNameStillWins()
        {
            write("video.mp4", "right");
            write("video.avi", "wrong");

            Assert.That(store().Get("video.mp4"), Is.EqualTo(System.Text.Encoding.UTF8.GetBytes("right")));
        }

        // The reported bug, in miniature.
        [Test]
        public void AVideoReferencedAsAviResolvesToTheMp4ThatActuallyShipped()
        {
            write("【maimai】Starlight Disco MV.mp4", "video");

            Assert.That(store().Get(@"【maimai】Starlight Disco MV.avi"), Is.Not.Null);
        }

        [Test]
        public void TheSameHoldsForOtherContainersMappersShipped()
        {
            write("clip.mp4");

            var s = store();

            Assert.That(s.Get("clip.flv"), Is.Not.Null);
            Assert.That(s.Get("clip.wmv"), Is.Not.Null);
            Assert.That(s.Get("clip.mpg"), Is.Not.Null);
        }

        // Kind-scoped on purpose: a sprite that is genuinely absent must stay absent rather than
        // quietly resolving to whatever unrelated media shares its name.
        [Test]
        public void AMissingSpriteDoesNotResolveToAVideo()
        {
            write("thing.mp4");

            Assert.That(store().Get("thing.png"), Is.Null);
        }

        [Test]
        public void AMissingVideoDoesNotResolveToAnImage()
        {
            write("thing.png");

            Assert.That(store().Get("thing.avi"), Is.Null);
        }

        // Existing behaviour that must survive: old-school storyboards reference sprites with no
        // extension at all.
        [Test]
        public void AnExtensionLessSpriteStillGuessesImageExtensions()
        {
            write("sprite.png");

            Assert.That(store().Get("sprite"), Is.Not.Null);
        }

        [Test]
        public void SomethingGenuinelyAbsentIsStillAbsent()
        {
            write("other.mp4");

            Assert.That(store().Get("missing.avi"), Is.Null);
            Assert.That(store().GetStream("missing.avi"), Is.Null);
        }

        // Paths in a .osu are Windows-flavoured and case-careless; that normalisation predates this
        // change and has to keep working alongside it.
        [Test]
        public void BackslashesAndCasingStillNormalise()
        {
            Directory.CreateDirectory(Path.Combine(dir, "sb"));
            File.WriteAllText(Path.Combine(dir, "sb", "Clip.mp4"), "x");

            Assert.That(store().Get(@"SB\clip.avi"), Is.Not.Null);
        }
    }
}
